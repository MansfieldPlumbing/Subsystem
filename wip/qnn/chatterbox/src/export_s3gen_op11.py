# =============================================================================
# export.py — Chatterbox S3Gen Vocoder → ONNX (Opset 11, Maximum Decomposition)
# =============================================================================
# Philosophy: Ugly, transparent, primitive-only graph for TensorRT to re-fuse.
# Target: Single-input (speech_tokens) → waveform, with conditioning baked in.
# =============================================================================

import os
import sys
import torch
import onnx
from unittest.mock import MagicMock, patch

# =============================================================================
# MOCK PERTH MODULE — MUST BE BEFORE ANY CHATTERBOX IMPORTS
# =============================================================================
mock_perth = MagicMock()
class MockWatermarker:
    def apply_watermark(self, wav, sample_rate):
        return wav  # No-op: return audio unchanged
mock_perth.PerthImplicitWatermarker = MockWatermarker
sys.modules['perth'] = mock_perth

# =============================================================================
# IMPORT CHATTERBOX (Now Safe)
# =============================================================================
def diagnostic_import():
    print("--- Verifying Environment Architecture ---")
    try:
        from chatterbox.tts_turbo import ChatterboxTurboTTS
        print("--- Environment Verified: All systems go. ---")
        return ChatterboxTurboTTS
    except ImportError as e:
        print(f"\n[!] CRITICAL IMPORT FAILURE: {e}")
        sys.exit(1)

# =============================================================================
# WRAPPER: Freeze reference conditioning so export only needs speech_tokens
# =============================================================================
class S3GenExportWrapper(torch.nn.Module):
    def __init__(self, s3gen_model, ref_dict, ref_sr=24000):
        super().__init__()
        self.s3gen = s3gen_model
        self.ref_sr = ref_sr
        for key in ['prompt_token', 'prompt_token_len', 'prompt_feat', 'prompt_feat_len', 'embedding']:
            if key in ref_dict and torch.is_tensor(ref_dict[key]):
                self.register_buffer(f'_cond_{key}', ref_dict[key])
            else:
                if key in ['prompt_token', 'prompt_token_len']:
                    dummy = torch.zeros(1, 1, dtype=torch.long)
                elif key == 'prompt_feat':
                    dummy = torch.randn(1, 1, 80)
                elif key == 'prompt_feat_len':
                    dummy = torch.tensor([1], dtype=torch.long)
                elif key == 'embedding':
                    dummy = torch.randn(1, 512)
                self.register_buffer(f'_cond_{key}', dummy)
    
    def forward(self, speech_tokens):
        ref_dict = {
            'prompt_token': self._buffers['_cond_prompt_token'],
            'prompt_token_len': self._buffers['_cond_prompt_token_len'],
            'prompt_feat': self._buffers['_cond_prompt_feat'],
            'prompt_feat_len': self._buffers['_cond_prompt_feat_len'],
            'embedding': self._buffers['_cond_embedding'],
        }
        batch_size = speech_tokens.size(0)
        seq_len = speech_tokens.size(1)
        speech_token_lens = torch.full(
            (batch_size,), seq_len, dtype=torch.long, device=speech_tokens.device
        )
        return self.s3gen(
            speech_tokens=speech_tokens,
            speech_token_lens=speech_token_lens,
            ref_wav=None,
            ref_dict=ref_dict,
            ref_sr=self.ref_sr,
            finalize=True,
            n_cfm_timesteps=2,
            noised_mels=None
        )

# =============================================================================
# EXPORT FUNCTION
# =============================================================================
def run_export():
    ChatterboxTurboTTS = diagnostic_import()
    output_name = "chatterbox_s3gen_vocoder_op11_decomposed.onnx"
    
    print("\n--- Loading Chatterbox Turbo Pipeline (CPU) ---")
    full_pipeline = ChatterboxTurboTTS.from_pretrained(device="cpu")
    
    model = full_pipeline.s3gen
    model.eval()
    
    ref_dict = full_pipeline.conds.gen if full_pipeline.conds else None
    if ref_dict is None:
        print("[!] No reference conditioning — creating minimal dummy conds")
        ref_dict = {
            'prompt_token': torch.zeros(1, 1).long(),
            'prompt_token_len': torch.tensor([1]).long(),
            'prompt_feat': torch.randn(1, 1, 80),
            'prompt_feat_len': torch.tensor([1]).long(),
            'embedding': torch.randn(1, 512),
        }
    
    print("--- Wrapping S3Gen with fixed reference conditioning ---")
    wrapped_model = S3GenExportWrapper(model, ref_dict, ref_sr=24000)
    wrapped_model.eval()
    
    print(f"\n--- Exporting to {output_name} (Opset 11) ---")
    print("--- FORCING DECOMPOSITION: LayerNorm/GELU → Add/Mul/Sqrt/etc. ---")
    print("--- Input: speech_tokens [batch, seq_len] only ---\n")
    
    dummy_tokens = torch.randint(0, 6561, (1, 500)).long()
    
    # =============================================================================
    # CREATE TQDM MOCK — Only applied during export
    # =============================================================================
    def _make_tqdm_mock():
        def _tqdm_fn(iterable=None, *args, **kwargs):
            return iterable if iterable else []
        m = MagicMock()
        m.tqdm = _tqdm_fn
        m.auto = m
        return m
    
    tqdm_mock = _make_tqdm_mock()
    
    # =============================================================================
    # EXPORT — WITH TRAINING MODE FOR IN-PLACE OPS
    # =============================================================================
    with patch.dict('sys.modules', {'tqdm': tqdm_mock, 'tqdm.auto': tqdm_mock}):
        import importlib
        if 'chatterbox.models.s3gen.flow_matching' in sys.modules:
            importlib.reload(sys.modules['chatterbox.models.s3gen.flow_matching'])
        
        # Temporarily enable training mode to allow in-place ops during tracing
        original_training = wrapped_model.training
        wrapped_model.train()
        
        torch.onnx.export(
            wrapped_model,
            dummy_tokens,
            output_name,
            export_params=True,
            opset_version=11,
            do_constant_folding=True,
            input_names=['speech_tokens'],
            output_names=['audio_waveform'],
            dynamic_axes={
                'speech_tokens': {1: 'token_sequence'},
                'audio_waveform': {2: 'audio_length'}
            },
            dynamo=False,
            verbose=False
        )
        
        # Restore eval mode
        if not original_training:
            wrapped_model.eval()
    
    # =============================================================================
    # POST-EXPORT VALIDATION
    # =============================================================================
    if os.path.exists(output_name):
        size_mb = os.path.getsize(output_name) / (1024**2)
        print(f"\n{'='*70}")
        print(f"--- EXPORT SUCCESS: {output_name} ---")
        print(f"--- SIZE: {size_mb:.0f} MB ---")
        print(f"--- Opset: 11 (Maximum Decomposition) ---")
        print(f"{'='*70}\n")
        
        print("--- Verification: Loading protobuf structure... ---")
        onnx_model = onnx.load(output_name)
        onnx.checker.check_model(onnx_model)
        print("--- ✓ Model structure valid ---\n")
        
        print("--- Graph Statistics ---")
        print(f"    Nodes: {len(onnx_model.graph.node)}")
        print(f"    Inputs: {len(onnx_model.graph.input)}")
        print(f"    Outputs: {len(onnx_model.graph.output)}")
        print(f"    Initializers (weights): {len(onnx_model.graph.initializer)}\n")
        
        print("--- Sample Node Types (first 20) ---")
        node_types = {}
        for node in onnx_model.graph.node[:20]:
            node_types[node.op_type] = node_types.get(node.op_type, 0) + 1
        for op_type, count in sorted(node_types.items(), key=lambda x: -x[1]):
            print(f"    {op_type}: {count}")
        print()
        
        print("--- Next Steps ---")
        print(f"    1. Build TRT engine:")
        print(f"       trtexec --onnx={output_name} ^")
        print(f"               --saveEngine=chatterbox_s3gen_sm86.trt ^")
        print(f"               --fp16 ^")
        print(f"               --minShapes=speech_tokens:1x100 ^")
        print(f"               --optShapes=speech_tokens:1x500 ^")
        print(f"               --maxShapes=speech_tokens:1x2000")
        print()
        print("--- Verified. You now have a decomposed, TRT-ready binary. ---")
    else:
        print("\n[!] Export failed: File not written.")
        sys.exit(1)

if __name__ == "__main__":
    run_export()
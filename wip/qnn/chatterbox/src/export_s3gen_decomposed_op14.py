# =============================================================================
# export.py — Chatterbox S3Gen Vocoder → ONNX (Opset 14, Maximum Decomposition)
# =============================================================================
# Philosophy: Ugly, transparent, primitive-only graph for TensorRT to re-fuse.
# Target: Single-input (speech_tokens) → waveform, with conditioning baked in.
# =============================================================================

import os
import sys
import warnings
import torch
import torch.nn.functional as F

# =============================================================================
# 1. THE "UGLY INEFFICIENT" STFT/ISTFT DECOMPOSITION HACK
# =============================================================================
def fake_complex(real, imag):
    return torch.stack([real, imag], dim=-1)

_orig_view_as_real = torch.view_as_real
def fake_view_as_real(tensor):
    if tensor.is_complex(): return _orig_view_as_real(tensor)
    if tensor.shape[-1] == 2: return tensor
    return _orig_view_as_real(tensor)

_orig_view_as_complex = torch.view_as_complex
def fake_view_as_complex(tensor):
    if tensor.is_complex(): return tensor
    if tensor.shape[-1] == 2: return tensor
    return _orig_view_as_complex(tensor)

def brutal_decomposed_stft(input, n_fft, hop_length=None, win_length=None, window=None,
                           center=True, pad_mode='reflect', normalized=False,
                           onesided=None, return_complex=None):
    if hop_length is None: hop_length = n_fft // 4
    if win_length is None: win_length = n_fft
    if onesided is None: onesided = True

    # SHAPE-AGNOSTIC FLATTENING (Kills the onnx::If bug)
    batch_shape = input.shape[:-1]
    x = input.reshape(-1, input.shape[-1])

    if center:
        pad_amount = n_fft // 2
        x = F.pad(x.unsqueeze(1), (pad_amount, pad_amount), mode=pad_mode).squeeze(1)

    if window is None: window = torch.ones(win_length, device=input.device, dtype=input.dtype)
    if win_length < n_fft:
        pad_left = (n_fft - win_length) // 2
        pad_right = n_fft - win_length - pad_left
        window = F.pad(window, (pad_left, pad_right))

    num_freqs = n_fft // 2 + 1 if onesided else n_fft
    k = torch.arange(num_freqs, device=input.device, dtype=input.dtype).unsqueeze(1)
    n = torch.arange(n_fft, device=input.device, dtype=input.dtype).unsqueeze(0)
    
    arg = -2.0 * torch.pi * k * n / n_fft
    real_weights = torch.cos(arg) * window.unsqueeze(0)
    imag_weights = torch.sin(arg) * window.unsqueeze(0)
    conv_weights = torch.cat([real_weights, imag_weights], dim=0).unsqueeze(1)
    
    x = x.unsqueeze(1)
    out = F.conv1d(x, conv_weights, stride=hop_length)
    
    frames = out.shape[-1]
    out = out.view(-1, 2, num_freqs, frames)
    out = out.permute(0, 2, 3, 1).contiguous() 
    
    if normalized: out = out / (n_fft ** 0.5)
    
    # SHAPE-AGNOSTIC UNPACKING
    out = out.reshape(*batch_shape, num_freqs, frames, 2)
        
    if return_complex is not False:
        return torch.complex(out[..., 0], out[..., 1])
    return out

def brutal_decomposed_istft(input, n_fft, hop_length=None, win_length=None, window=None,
                            center=True, normalized=False, onesided=None, length=None, return_complex=False):
    if hop_length is None: hop_length = n_fft // 4
    if win_length is None: win_length = n_fft
    if onesided is None: onesided = True

    if input.is_complex():
        real_in, imag_in = input.real, input.imag
    else:
        real_in, imag_in = input[..., 0], input[..., 1]
        
    # SHAPE-AGNOSTIC FLATTENING (Kills the onnx::If bug)
    batch_shape = real_in.shape[:-2]
    frames = real_in.shape[-1]
    static_num_freqs = (n_fft // 2 + 1) if onesided else n_fft

    real_in = real_in.reshape(-1, static_num_freqs, frames)
    imag_in = imag_in.reshape(-1, static_num_freqs, frames)

    if window is None: window = torch.ones(win_length, device=real_in.device, dtype=real_in.dtype)
    if win_length < n_fft:
        pad_left = (n_fft - win_length) // 2
        pad_right = n_fft - win_length - pad_left
        window = F.pad(window, (pad_left, pad_right))

    k = torch.arange(static_num_freqs, device=real_in.device, dtype=real_in.dtype).unsqueeze(1)
    n = torch.arange(n_fft, device=real_in.device, dtype=real_in.dtype).unsqueeze(0)
    arg = 2.0 * torch.pi * k * n / n_fft
    
    c = torch.ones(static_num_freqs, 1, device=real_in.device, dtype=real_in.dtype)
    if onesided:
        c[1:-1] = 2.0
        if n_fft % 2 == 1: c[-1] = 2.0

    real_weights = (c / n_fft) * torch.cos(arg) * window.unsqueeze(0)
    imag_weights = -(c / n_fft) * torch.sin(arg) * window.unsqueeze(0)
    conv_weights = torch.cat([real_weights.unsqueeze(1), imag_weights.unsqueeze(1)], dim=0)

    x = torch.cat([real_in, imag_in], dim=1) # [B, 2F, T]
    out = F.conv_transpose1d(x, conv_weights, stride=hop_length)
    
    window_sq = (window ** 2).unsqueeze(0).unsqueeze(0)
    dummy_in = torch.ones(1, 1, frames, device=x.device, dtype=x.dtype)
    w_ola = F.conv_transpose1d(dummy_in, window_sq, stride=hop_length)
    w_ola = torch.clamp(w_ola, min=1e-11)
    out = out / w_ola
    
    if center:
        pad = n_fft // 2
        out = out[..., pad : out.shape[-1] - pad]
    if length is not None:
        out = out[..., :length]
    if normalized:
        out = out * (n_fft ** 0.5)

    # SHAPE-AGNOSTIC UNPACKING
    # NOTE: No squeeze(1) here — squeeze creates an onnx::If with mismatched
    # output ranks (Float(*,*) vs Float(*,*,*)) that breaks type inference in
    # the ONNX symbolic lowering pass. reshape(*batch_shape, -1) handles the
    # batch dim collapse unconditionally and keeps the graph rank-stable.
    out = out.reshape(*batch_shape, -1)

    return out

# =============================================================================
# GLOBAL HIJACK BLOCK
# =============================================================================
torch.complex = fake_complex
torch.view_as_real = fake_view_as_real
torch.view_as_complex = fake_view_as_complex
torch.stft = brutal_decomposed_stft
torch.istft = brutal_decomposed_istft

# FIX FOR THE ALIAS TYPO BUGS IN PYTORCH
torch.concat = torch.cat                  
torch.clip = torch.clamp
torch.Tensor.clip = torch.Tensor.clamp

torch.Tensor.stft = brutal_decomposed_stft
torch.Tensor.istft = brutal_decomposed_istft
torch.Tensor.view_as_real = fake_view_as_real
torch.Tensor.view_as_complex = fake_view_as_complex

# =============================================================================
# 2. DOWNGRADE INFERENCE MODE TO NO_GRAD GLOBALLY
# =============================================================================
class SafeInferenceMode:
    def __init__(self, mode=True):
        self.mode = mode
        self._ctx = torch.no_grad() if mode else None
    def __call__(self, func):
        import functools
        @functools.wraps(func)
        def wrapper(*args, **kwargs):
            with torch.no_grad(): return func(*args, **kwargs)
        return wrapper
    def __enter__(self):
        if self.mode: self._ctx.__enter__()
        return self
    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.mode: self._ctx.__exit__(exc_type, exc_val, exc_tb)

torch.inference_mode = SafeInferenceMode

# =============================================================================
# 3. MOCK PERTH & DIAGNOSTICS
# =============================================================================
import onnx
from unittest.mock import MagicMock, patch

mock_perth = type('MagicMock', (), {'__getattr__': lambda *a, **kw: MagicMock()})()
class MockWatermarker:
    def apply_watermark(self, wav, sample_rate): return wav
mock_perth.PerthImplicitWatermarker = MockWatermarker
sys.modules['perth'] = mock_perth

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
# 4. WRAPPER & TENSOR SCRUBBING
# =============================================================================
class S3GenExportWrapper(torch.nn.Module):
    def __init__(self, s3gen_model, ref_dict, ref_sr=24000):
        super().__init__()
        self.s3gen = s3gen_model
        self.ref_sr = ref_sr
        
        keys_and_dummies = {
            'prompt_token': torch.zeros(1, 1, dtype=torch.long),
            'prompt_token_len': torch.tensor([1], dtype=torch.long),
            'prompt_feat': torch.randn(1, 1, 80),
            'prompt_feat_len': torch.tensor([1], dtype=torch.long),
            'embedding': torch.randn(1, 512)
        }
        
        for key, dummy in keys_and_dummies.items():
            val = ref_dict.get(key)
            if val is None: val = dummy
            if not torch.is_tensor(val): val = torch.tensor(val)
            self.register_buffer(f'_cond_{key}', val)
    
    def forward(self, speech_tokens):
        ref_dict = {
            'prompt_token': getattr(self, '_cond_prompt_token'),
            'prompt_token_len': getattr(self, '_cond_prompt_token_len'),
            'prompt_feat': getattr(self, '_cond_prompt_feat'),
            'prompt_feat_len': getattr(self, '_cond_prompt_feat_len'),
            'embedding': getattr(self, '_cond_embedding')
        }
        slen = torch.full((speech_tokens.size(0),), speech_tokens.size(1), dtype=torch.long, device=speech_tokens.device)
        return self.s3gen(
            speech_tokens=speech_tokens, 
            speech_token_lens=slen, 
            ref_wav=None, 
            ref_dict=ref_dict, 
            ref_sr=self.ref_sr, 
            finalize=True, 
            n_cfm_timesteps=2
        )

def run_export():
    ChatterboxTurboTTS = diagnostic_import()
    output_name = "chatterbox_s3gen_vocoder_op14_decomposed.onnx"
    
    print("\n--- Loading Pipeline ---")
    full_pipeline = ChatterboxTurboTTS.from_pretrained(device="cpu")
    
    # GLOBALLY LOBOTOMIZE TQDM
    import tqdm.std
    tqdm.std.tqdm.__iter__ = lambda self: iter(self.iterable) if self.iterable is not None else iter([])
    tqdm.std.tqdm.update = lambda *args, **kwargs: None
    tqdm.std.tqdm.close = lambda *args, **kwargs: None
    tqdm.std.tqdm.display = lambda *args, **kwargs: None
    
    cond_dict = full_pipeline.conds.gen if full_pipeline.conds else {}
    wrapped_model = S3GenExportWrapper(full_pipeline.s3gen, cond_dict)
    
    print("--- Scrubbing Inference Flags ---")
    def eradicate(m):
        for n, p in m.named_parameters(recurse=False):
            if p is not None: setattr(m, n, torch.nn.Parameter(p.clone().detach()))
        for n, b in m.named_buffers(recurse=False):
            if b is not None: setattr(m, n, b.clone().detach())
        for c in m.children(): eradicate(c)
            
    eradicate(wrapped_model)
    wrapped_model.eval()
    
    dummy_tokens = torch.randint(0, 6561, (1, 500)).long()

    # NEUTRALIZE trim_fade — the in-place index_put_ on s3gen.py:296 produces
    # onnx::Placeholder nodes the exporter can't lower. Setting to all-ones
    # makes the slice-assign a no-op that constant-folds away cleanly.
    print("--- Neutralizing trim_fade (kills index_put_ placeholders) ---")
    for buf_name, buf in wrapped_model.s3gen.named_buffers():
        if 'trim_fade' in buf_name:
            print(f"    neutralizing buffer: {buf_name}  shape={buf.shape}")
            buf.fill_(1.0)
    for param_name, param in wrapped_model.s3gen.named_parameters():
        if 'trim_fade' in param_name:
            print(f"    neutralizing param:  {param_name}  shape={param.shape}")
            param.data.fill_(1.0)

    print("--- Forcing Legacy TorchScript Tracer (Bypassing Dynamo) ---")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        traced_model = torch.jit.trace(wrapped_model, dummy_tokens, strict=False)
    
    print(f"--- Exporting to {output_name} ---")
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        torch.onnx.export(
            traced_model, dummy_tokens, output_name,
            export_params=True, 
            opset_version=14, 
            do_constant_folding=True,
            input_names=['speech_tokens'], 
            output_names=['audio_waveform'],
            dynamic_axes={'speech_tokens': {1: 'seq'}, 'audio_waveform': {1: 'len'}},
            dynamo=False  # Forces primitive execution.
        )
    
    if os.path.exists(output_name):
        print(f"\n--- SUCCESS: {output_name} ({os.path.getsize(output_name)/(1024**2):.0f} MB) ---")
        onnx.checker.check_model(onnx.load(output_name))
        print("--- Node Count:", len(onnx.load(output_name).graph.node), "---")

if __name__ == "__main__":
    run_export()
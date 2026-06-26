"""
match_blobs_to_ops.py
Align the 5,877 weight-bearing ops to the 9,946 blob candidates by walking
both lists in order and matching on expected byte size.

SD 1.5 UNet weight sizes (U8 quantized):
  Conv:  Cout * Cin * kH * kW
  Gemm:  Cout * Cin
  GroupNorm / LayerNorm:  2 * Cchannels  (gamma + beta, but stored as FP16/FP32)

The compiler shards some ops (e.g. attn1_to_q_convs_0..3) so we infer per-shard
sizes by counting siblings of the same base name.

Output: weight_map.tsv with (op_id, op_name, expected_size, blob_idx, actual_size, offset_hex)
"""

import re
from collections import defaultdict

WEIGHT_OPS_PATH = r'C:\bin\qnn\unet_out\weight_ops.txt'
BLOB_MAP_PATH   = r'C:\bin\qnn\unet_out\blob_map.tsv'
OUT_PATH        = r'C:\bin\qnn\unet_out\weight_map.tsv'

# Standard SD 1.5 UNet channel counts per stage
# down_blocks: 0=320, 1=640, 2=1280, 3=1280
# mid_block: 1280
# up_blocks: 0=1280, 1=1280, 2=640, 3=320
STAGE_CHANNELS = {
    'down_blocks_0': 320,
    'down_blocks_1': 640,
    'down_blocks_2': 1280,
    'down_blocks_3': 1280,
    'mid_block':     1280,
    'up_blocks_0':   1280,
    'up_blocks_1':   1280,
    'up_blocks_2':   640,
    'up_blocks_3':   320,
}

# Text embedding dim and time embedding dim
TEXT_EMB_DIM = 768
TIME_EMB_DIM = 1280


def get_stage(op_name):
    """Identify which UNet stage an op belongs to."""
    for stage in STAGE_CHANNELS:
        if stage in op_name:
            return stage, STAGE_CHANNELS[stage]
    if 'conv_in' in op_name:
        return 'conv_in', 320
    if 'conv_out' in op_name:
        return 'conv_out', 320
    if 'time_embedding' in op_name or 'time_emb' in op_name:
        return 'time_emb', TIME_EMB_DIM
    if 'conv_norm_out' in op_name:
        return 'conv_norm_out', 320
    return 'unknown', 0


def estimate_size(op_name, op_id):
    """Best-effort estimate of weight blob size in bytes (U8)."""
    stage, ch = get_stage(op_name)

    # conv_in: 4 -> 320, 3x3
    if op_name == '_conv_in_Conv':
        return 4 * 320 * 3 * 3  # 11,520

    # conv_out: 320 -> 4, 3x3
    if op_name == '_conv_out_Conv':
        return 320 * 4 * 3 * 3  # 11,520

    # time_embedding linears
    if 'time_embedding_linear_1_Gemm' in op_name:
        return 320 * 1280  # 409,600
    if 'time_embedding_linear_2_Gemm' in op_name:
        return 1280 * 1280  # 1,638,400

    # ResNet conv1/conv2: ch x ch x 3 x 3
    if re.search(r'resnets_\d+_conv[12]_Conv$', op_name):
        return ch * ch * 9

    # ResNet time_emb_proj: 1280 -> ch
    if 'time_emb_proj_Gemm' in op_name:
        return TIME_EMB_DIM * ch

    # Downsample / Upsample conv: ch x ch x 3 x 3
    if 'downsamplers' in op_name and op_name.endswith('_Conv'):
        return ch * ch * 9
    if 'upsamplers' in op_name and op_name.endswith('_Conv'):
        return ch * ch * 9

    # Attention proj_in / proj_out: ch x ch x 1 x 1
    if op_name.endswith('proj_in_Conv') or op_name.endswith('proj_out_Conv'):
        return ch * ch

    # Self-attention to_q/k/v sharded (4 shards typically)
    # Each shard: ch x (ch/4) x 1 x 1
    if re.search(r'attn1_to_[qkv]_convs_\d+_Conv$', op_name):
        return ch * (ch // 4)

    # Cross-attention to_q (from latent, ch -> ch)
    if re.search(r'attn2_to_q_convs_\d+_Conv$', op_name):
        return ch * (ch // 4)

    # Cross-attention to_k/v (from text emb 768 -> ch)
    if re.search(r'attn2_to_[kv]_convs_\d+_Conv$', op_name):
        return TEXT_EMB_DIM * (ch // 4)

    # to_out
    if re.search(r'attn[12]_to_out_\d+_Conv$', op_name):
        return ch * ch

    # FF net (GEGLU): proj 1->2*4*ch, then 4*ch->ch
    if 'ff_net_0_proj_MatMul' in op_name:
        return ch * (8 * ch)  # GEGLU expansion
    if 'ff_net_2_MatMul' in op_name:
        return (4 * ch) * ch

    # Norms: gamma + beta, FP16 typically (2 bytes each)
    if 'GroupNorm' in op_name or 'LayerNorm' in op_name or 'norm' in op_name.lower():
        return ch * 2 * 2  # 2 vectors, FP16

    # Bare MatMul without weights (attention QK^T and softmax*V) — no weights
    if 'matmul_1' in op_name or 'matmul_2' in op_name:
        return 0

    return -1  # unknown


def main():
    # Load weight ops in execution order
    weight_ops = []
    with open(WEIGHT_OPS_PATH, 'r') as f:
        for line in f:
            parts = line.strip().split('\t')
            if len(parts) >= 3:
                op_id, cycles, name = parts[0], parts[1], parts[2]
                weight_ops.append((int(op_id), int(cycles), name))

    # Load blobs in offset order
    blobs = []
    with open(BLOB_MAP_PATH, 'r') as f:
        next(f)  # header
        for line in f:
            parts = line.strip().split('\t')
            if len(parts) >= 4:
                idx, off_hex, off_dec, size = parts[0], parts[1], parts[2], parts[3]
                blobs.append((int(idx), int(off_dec), int(size), off_hex))

    print(f'Weight ops: {len(weight_ops):,}')
    print(f'Blobs:      {len(blobs):,}')
    print()

    # Estimate sizes for all ops
    estimated = []
    skipped_zero = 0
    skipped_unknown = 0
    for op_id, cycles, name in weight_ops:
        sz = estimate_size(name, op_id)
        if sz == 0:
            skipped_zero += 1
            continue
        if sz < 0:
            skipped_unknown += 1
            estimated.append((op_id, cycles, name, None))
            continue
        estimated.append((op_id, cycles, name, sz))

    print(f'Ops with estimated size: {sum(1 for e in estimated if e[3] is not None):,}')
    print(f'Ops skipped (matmul/no weights): {skipped_zero:,}')
    print(f'Ops with unknown shape: {skipped_unknown:,}')
    print()

    # Walk both lists in order, match by approximate size (within 5% or padded to alignment)
    matches = []
    blob_idx = 0
    unmatched = 0

    print('First 40 matches:')
    print(f'{"op_id":>6}  {"expected":>10}  {"actual":>10}  {"offset":>10}  {"name"}')
    print('-' * 100)

    for op_id, cycles, name, expected in estimated:
        if expected is None:
            continue
        # Find next blob within size tolerance
        # HMX padding rounds up; actual blob size should be >= expected
        # but not much larger (otherwise we're skipping over real weights)
        found = None
        scan_limit = min(blob_idx + 10, len(blobs))
        for j in range(blob_idx, scan_limit):
            _, off, size, off_hex = blobs[j]
            # Accept if blob size is within [expected, expected * 1.5]
            # (accounts for alignment padding and per-channel scale table merge)
            if size >= expected * 0.9 and size <= expected * 2.0:
                found = (j, off, size, off_hex)
                break
        if found:
            j, off, size, off_hex = found
            matches.append((op_id, name, expected, j, size, off_hex))
            if len(matches) <= 40:
                print(f'{op_id:>6}  {expected:>10,}  {size:>10,}  {off_hex:>10}  {name}')
            blob_idx = j + 1
        else:
            unmatched += 1
            if unmatched <= 5:
                print(f'{op_id:>6}  {expected:>10,}  {"---":>10}  {"---":>10}  UNMATCHED: {name}')

    print()
    print(f'Total matches: {len(matches):,}')
    print(f'Unmatched ops: {unmatched:,}')
    print(f'Blob walker advanced to: {blob_idx} / {len(blobs)}')
    print()

    with open(OUT_PATH, 'w') as f:
        f.write('op_id\top_name\texpected_size\tblob_idx\tactual_size\toffset_hex\n')
        for op_id, name, expected, j, size, off_hex in matches:
            f.write(f'{op_id}\t{name}\t{expected}\t{j}\t{size}\t{off_hex}\n')

    print(f'Wrote weight map to {OUT_PATH}')


if __name__ == '__main__':
    main()
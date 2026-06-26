"""
match_iter.py
Iterative blob-to-op matcher that keeps going over the data,
learning weight blob sizes from matches and re-running until stable.

Strategy:
  - Pass 1: Match by hardcoded size estimates (the easy ones)
  - Pass 2+: For unmatched ops, look at the gaps in the blob map between
             matched neighbors and infer the unmatched op's size from
             the available space and op-class similarity to known matches
  - Stop when no new matches are found in an iteration

Output: weight_map.tsv refined across iterations
"""

import re
from collections import defaultdict

WEIGHT_OPS_PATH = r'C:\bin\qnn\unet_out\weight_ops.txt'
BLOB_MAP_PATH   = r'C:\bin\qnn\unet_out\blob_map.tsv'
OUT_PATH        = r'C:\bin\qnn\unet_out\weight_map.tsv'

STAGE_CHANNELS = {
    'down_blocks_0': 320, 'down_blocks_1': 640,
    'down_blocks_2': 1280, 'down_blocks_3': 1280,
    'mid_block':     1280,
    'up_blocks_0':   1280, 'up_blocks_1':   1280,
    'up_blocks_2':   640,  'up_blocks_3':   320,
}
TEXT_EMB_DIM = 768
TIME_EMB_DIM = 1280


def get_stage_channels(op_name):
    for stage, ch in STAGE_CHANNELS.items():
        if stage in op_name:
            return ch
    if 'conv_in' in op_name or 'conv_out' in op_name or 'conv_norm_out' in op_name:
        return 320
    if 'time_embedding' in op_name or 'time_emb' in op_name:
        return TIME_EMB_DIM
    return None


def op_class(op_name):
    """Categorize op for similarity-based matching."""
    if 'GroupNorm' in op_name: return 'group_norm'
    if 'LayerNorm' in op_name: return 'layer_norm'
    if op_name.endswith('_conv_in_Conv') or op_name == '_conv_in_Conv': return 'conv_in'
    if op_name == '_conv_out_Conv': return 'conv_out'
    if 'time_embedding_linear' in op_name: return 'time_emb_linear'
    if re.search(r'resnets_\d+_conv[12]_Conv$', op_name): return 'resnet_conv'
    if 'time_emb_proj_Gemm' in op_name: return 'time_emb_proj'
    if 'downsamplers' in op_name and op_name.endswith('_Conv'): return 'downsample'
    if 'upsamplers' in op_name and op_name.endswith('_Conv'): return 'upsample'
    if op_name.endswith('proj_in_Conv'): return 'proj_in'
    if op_name.endswith('proj_out_Conv'): return 'proj_out'
    if re.search(r'attn1_to_[qkv]_convs_\d+_Conv$', op_name): return 'self_attn_qkv'
    if re.search(r'attn2_to_q_convs_\d+_Conv$', op_name): return 'cross_attn_q'
    if re.search(r'attn2_to_[kv]_convs_\d+_Conv$', op_name): return 'cross_attn_kv'
    if re.search(r'to_out_\d+_Conv$', op_name): return 'to_out'
    if 'ff_net_0_proj' in op_name: return 'ff_geglu_proj'
    if 'ff_net_2' in op_name: return 'ff_out'
    if 'MatMul' in op_name: return 'matmul_no_weight'
    return 'unknown'


def estimate_size(op_name):
    cls = op_class(op_name)
    ch = get_stage_channels(op_name)
    if cls == 'matmul_no_weight': return 0
    if cls in ('group_norm', 'layer_norm'): return 2112  # learned from pass 1
    if cls == 'conv_in': return 11520
    if cls == 'conv_out': return 11520
    if cls == 'time_emb_linear':
        if 'linear_1' in op_name: return 320 * 1280
        return 1280 * 1280
    if cls == 'resnet_conv' and ch: return ch * ch * 9
    if cls == 'time_emb_proj' and ch: return TIME_EMB_DIM * ch
    if cls in ('downsample', 'upsample') and ch: return ch * ch * 9
    if cls in ('proj_in', 'proj_out') and ch: return ch * ch
    if cls == 'self_attn_qkv' and ch: return ch * (ch // 4)
    if cls == 'cross_attn_q' and ch: return ch * (ch // 4)
    if cls == 'cross_attn_kv' and ch: return TEXT_EMB_DIM * (ch // 4)
    if cls == 'to_out' and ch: return ch * ch
    if cls == 'ff_geglu_proj' and ch: return ch * (8 * ch)
    if cls == 'ff_out' and ch: return (4 * ch) * ch
    return None


def load_ops():
    ops = []
    with open(WEIGHT_OPS_PATH, 'r') as f:
        for line in f:
            parts = line.strip().split('\t')
            if len(parts) >= 3:
                ops.append((int(parts[0]), int(parts[1]), parts[2]))
    return ops


def load_blobs():
    blobs = []
    with open(BLOB_MAP_PATH, 'r') as f:
        next(f)
        for line in f:
            parts = line.strip().split('\t')
            if len(parts) >= 4:
                blobs.append({
                    'idx': int(parts[0]),
                    'offset': int(parts[2]),
                    'size': int(parts[3]),
                    'offset_hex': parts[1],
                    'matched_op': None,
                })
    return blobs


def try_match(ops, blobs, size_tolerance=(0.85, 2.5)):
    """One matching pass. Returns count of new matches."""
    new_matches = 0
    blob_cursor = 0
    n_blobs = len(blobs)

    for op_id, cycles, name in ops:
        if hasattr(try_match, '_matched') and op_id in try_match._matched:
            continue
        expected = estimate_size(name)
        if expected is None or expected == 0:
            continue

        # Scan forward for a blob in size window that isn't already matched
        scan_end = min(blob_cursor + 30, n_blobs)
        for j in range(blob_cursor, scan_end):
            b = blobs[j]
            if b['matched_op'] is not None:
                continue
            lo, hi = expected * size_tolerance[0], expected * size_tolerance[1]
            if lo <= b['size'] <= hi:
                b['matched_op'] = (op_id, name, expected)
                try_match._matched.add(op_id)
                new_matches += 1
                blob_cursor = j + 1
                break
    return new_matches


def main():
    ops = load_ops()
    blobs = load_blobs()
    print(f'Loaded {len(ops):,} ops and {len(blobs):,} blobs')

    try_match._matched = set()

    # Iterate with progressively looser tolerance
    tolerances = [
        (0.95, 1.10),  # tight: exact match + small padding
        (0.85, 1.50),  # medium: significant padding allowed
        (0.50, 3.00),  # loose: scale tables fused in
    ]

    total_new = 0
    for iteration, tol in enumerate(tolerances, 1):
        new = try_match(ops, blobs, tol)
        total_new += new
        print(f'Pass {iteration} (tolerance {tol[0]:.2f}-{tol[1]:.2f}x): +{new:,} matches, total {total_new:,}')
        if new == 0:
            break

    # Keep iterating with loose tolerance until no progress
    last = total_new
    for iteration in range(4, 20):
        new = try_match(ops, blobs, (0.50, 3.00))
        if new == 0:
            print(f'Pass {iteration}: 0 new matches, stable')
            break
        total_new += new
        print(f'Pass {iteration}: +{new:,} matches, total {total_new:,}')

    print()
    print(f'Final: {total_new:,} ops matched out of {len(ops):,}')

    # Coverage by op class
    class_stats = defaultdict(lambda: [0, 0])  # [matched, total]
    for op_id, cycles, name in ops:
        cls = op_class(name)
        class_stats[cls][1] += 1
        if op_id in try_match._matched:
            class_stats[cls][0] += 1

    print()
    print('Match coverage by op class:')
    print(f'{"class":>20}  {"matched":>8}  {"total":>8}  {"pct":>6}')
    print('-' * 50)
    for cls in sorted(class_stats):
        m, t = class_stats[cls]
        pct = (100.0 * m / t) if t else 0
        print(f'{cls:>20}  {m:>8,}  {t:>8,}  {pct:>5.1f}%')

    # Write output
    with open(OUT_PATH, 'w') as f:
        f.write('blob_idx\toffset_hex\tactual_size\top_id\top_name\texpected_size\n')
        for b in blobs:
            if b['matched_op']:
                op_id, name, expected = b['matched_op']
                f.write(f'{b["idx"]}\t{b["offset_hex"]}\t{b["size"]}\t{op_id}\t{name}\t{expected}\n')

    matched_blobs = sum(1 for b in blobs if b['matched_op'])
    matched_bytes = sum(b['size'] for b in blobs if b['matched_op'])
    total_bytes = sum(b['size'] for b in blobs)
    print()
    print(f'Blobs matched: {matched_blobs:,} / {len(blobs):,}  ({100*matched_blobs/len(blobs):.1f}%)')
    print(f'Bytes matched: {matched_bytes/1e6:.1f} MB / {total_bytes/1e6:.1f} MB  ({100*matched_bytes/total_bytes:.1f}%)')

    # Show what's left unmatched
    print()
    print('Sample unmatched ops (first 20):')
    shown = 0
    for op_id, cycles, name in ops:
        if op_id in try_match._matched: continue
        if op_class(name) == 'matmul_no_weight': continue
        cls = op_class(name)
        est = estimate_size(name)
        print(f'  OpId_{op_id:>5}  [{cls:>20}]  estimated={est}  {name}')
        shown += 1
        if shown >= 20: break

    print()
    print('Sample unmatched blobs (>10KB, first 20):')
    shown = 0
    for b in blobs:
        if b['matched_op'] or b['size'] < 10240: continue
        print(f'  blob {b["idx"]:>5}  {b["offset_hex"]}  size={b["size"]:,}')
        shown += 1
        if shown >= 20: break


if __name__ == '__main__':
    main()
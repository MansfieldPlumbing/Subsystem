"""
extract_weights.py
Walks v340L_blueprint.txt to enumerate ops in execution order
and identifies weight-bearing layers (Conv/Gemm/GroupNorm).
"""

import re

BLUEPRINT_PATH = r'C:\bin\qnn\unet_out\v340L_blueprint.txt'

# Parse the blueprint
with open(BLUEPRINT_PATH, 'r', encoding='utf-8') as f:
    content = f.read()

# Match: <name>:OpId_<N> (cycles): <X> cycles
op_pattern = r'([_\w]+):OpId_(\d+)\s+\(cycles\):\s+(\d+)\s+cycles'
ops = re.findall(op_pattern, content)

print(f'Total ops found: {len(ops)}')
print()
print('First 50 ops in execution order:')
print('-' * 90)
for name, opid, cycles in ops[:50]:
    print(f'  OpId_{opid:>5}  {int(cycles):>12,} cyc   {name}')

# Filter weight-bearing ops
weight_ops = [op for op in ops if any(k in op[0] for k in ('Conv', 'Gemm', 'MatMul', 'GroupNorm', 'LayerNorm'))]

print()
print(f'Weight-bearing ops: {len(weight_ops)}')
print()
print('First 30 weight-bearing ops:')
print('-' * 90)
for name, opid, cycles in weight_ops[:30]:
    print(f'  OpId_{opid:>5}  {int(cycles):>12,} cyc   {name}')

# Save the weight-op list for next step
with open(r'C:\bin\qnn\unet_out\weight_ops.txt', 'w') as f:
    for name, opid, cycles in weight_ops:
        f.write(f'{opid}\t{cycles}\t{name}\n')

print()
print(f'Wrote {len(weight_ops)} weight ops to weight_ops.txt')
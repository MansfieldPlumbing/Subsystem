"""
walk_blobs.py
Scan unet.bin to identify weight blob boundaries.

Strategy: HMX-friendly weight blobs are typically aligned to 2KB or 4KB
boundaries with zero-padding between them. We scan for:
  1. Runs of zeros (>= 16 bytes) — likely alignment padding
  2. Entropy transitions (high -> low -> high) — boundary signals
  3. Aligned offsets (multiples of 2048 or 4096)

Output: blob_map.tsv with columns: idx, offset_hex, size_bytes, entropy_estimate
"""

import struct
import os

BIN_PATH = r'C:\bin\qnn\test-harness-s23-v73\unet.bin'
OUT_PATH = r'C:\bin\qnn\unet_out\blob_map.tsv'

# Skip the descriptor region (your minimap shows weights start at ~0x081000)
WEIGHT_START = 0x081000

# Minimum padding run length to consider a "boundary"
MIN_PAD_RUN = 16

# Minimum blob size to report (skip tiny scratch data)
MIN_BLOB_SIZE = 256


def quick_entropy(buf):
    """Cheap byte-distribution variance, not full Shannon. Higher = more random."""
    if len(buf) < 64:
        return 0.0
    # Sample 256 bytes, count unique values
    sample = buf[:256] if len(buf) >= 256 else buf
    return len(set(sample)) / 256.0


def main():
    print(f'Reading {BIN_PATH}...')
    with open(BIN_PATH, 'rb') as f:
        data = f.read()

    total = len(data)
    print(f'Total: {total:,} bytes ({total/1e6:.1f} MB)')
    print(f'Weight region starts at 0x{WEIGHT_START:08X}')
    print(f'Scanning for blob boundaries (zero-runs >= {MIN_PAD_RUN} bytes)...')
    print()

    blobs = []
    pos = WEIGHT_START

    # Skip leading zeros at the start of weight region
    while pos < total and data[pos] == 0:
        pos += 1

    blob_start = pos
    zero_run = 0

    while pos < total:
        if data[pos] == 0:
            zero_run += 1
        else:
            if zero_run >= MIN_PAD_RUN:
                # End of previous blob
                blob_end = pos - zero_run
                blob_size = blob_end - blob_start
                if blob_size >= MIN_BLOB_SIZE:
                    ent = quick_entropy(data[blob_start:blob_start + 256])
                    blobs.append((blob_start, blob_size, ent, zero_run))
                blob_start = pos
            zero_run = 0
        pos += 1

    # Final blob
    if zero_run >= MIN_PAD_RUN:
        blob_end = total - zero_run
    else:
        blob_end = total
    blob_size = blob_end - blob_start
    if blob_size >= MIN_BLOB_SIZE:
        ent = quick_entropy(data[blob_start:blob_start + 256])
        blobs.append((blob_start, blob_size, ent, 0))

    print(f'Found {len(blobs):,} candidate blobs')
    print()
    print('First 30 blobs:')
    print(f'{"idx":>5}  {"offset":>10}  {"size_bytes":>12}  {"size_MB":>8}  {"ent":>5}  {"pad_after":>10}')
    print('-' * 70)
    for i, (off, size, ent, pad) in enumerate(blobs[:30]):
        print(f'{i:>5}  0x{off:08X}  {size:>12,}  {size/1e6:>8.2f}  {ent:>5.2f}  {pad:>10}')

    # Write full map
    with open(OUT_PATH, 'w') as f:
        f.write('idx\toffset_hex\toffset_dec\tsize_bytes\tsize_mb\tentropy_est\tpad_after\n')
        for i, (off, size, ent, pad) in enumerate(blobs):
            f.write(f'{i}\t0x{off:08X}\t{off}\t{size}\t{size/1e6:.3f}\t{ent:.3f}\t{pad}\n')

    print()
    print(f'Wrote {len(blobs):,} blobs to {OUT_PATH}')

    # Histogram of blob sizes — sanity check
    size_buckets = {'<1KB': 0, '1-10KB': 0, '10-100KB': 0, '100KB-1MB': 0, '1-10MB': 0, '>10MB': 0}
    for off, size, ent, pad in blobs:
        if size < 1024: size_buckets['<1KB'] += 1
        elif size < 10240: size_buckets['1-10KB'] += 1
        elif size < 102400: size_buckets['10-100KB'] += 1
        elif size < 1048576: size_buckets['100KB-1MB'] += 1
        elif size < 10485760: size_buckets['1-10MB'] += 1
        else: size_buckets['>10MB'] += 1

    print()
    print('Blob size distribution:')
    for bucket, count in size_buckets.items():
        print(f'  {bucket:>12}: {count:>6,}')

    total_blob_bytes = sum(b[1] for b in blobs)
    print()
    print(f'Total blob bytes: {total_blob_bytes:,} ({total_blob_bytes/1e6:.1f} MB)')
    print(f'Weight region size: {total - WEIGHT_START:,} ({(total - WEIGHT_START)/1e6:.1f} MB)')
    print(f'Coverage: {100 * total_blob_bytes / (total - WEIGHT_START):.1f}%')


if __name__ == '__main__':
    main()
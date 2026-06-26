import numpy as np
import matplotlib.pyplot as plt

# Load your de-swizzled weights
w_linear = np.fromfile("C:/bin/llama-trace/gfx900_test/midblock_1280_LINEAR.bin", dtype=np.int8)
w_linear = w_linear.reshape((1280, 1280))

# Load the raw weights for comparison
w_raw = np.fromfile("C:/bin/llama-trace/gfx900_test/midblock_1280_s8.bin", dtype=np.int8)
w_raw = w_raw.reshape((1280, 1280))

fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(12, 6))

# Show a 128x128 pixel slice (4x4 Crouton tiles)
slice_size = 128

ax1.imshow(w_raw[:slice_size, :slice_size], cmap='gray')
ax1.set_title("RAW (Hexagon Layout)")

ax2.imshow(w_linear[:slice_size, :slice_size], cmap='gray')
ax2.set_title("DE-SWIZZLED (GFX900 Layout)")

plt.show()
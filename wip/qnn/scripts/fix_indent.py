with open("C:/bin/qnn/modeltest.py", "r") as f:
    lines = f.readlines()

start = -1
for i, l in enumerate(lines):
    if "# --- INJECTED BY POWERSHELL ---" in l:
        start = i
        break

if start != -1:
    # Get the indentation of the 'if' statement right before our block
    parent_indent = len(lines[start-1]) - len(lines[start-1].lstrip())
    needed = parent_indent + 4
    current = len(lines[start]) - len(lines[start].lstrip())
    shift = needed - current

    # Shift the rest of the block to the right
    if shift > 0:
        for i in range(start, len(lines)):
            if lines[i].strip():
                lines[i] = (" " * shift) + lines[i]

with open("C:/bin/qnn/modeltest.py", "w") as f:
    f.writelines(lines)

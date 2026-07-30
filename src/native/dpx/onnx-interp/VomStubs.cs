using System;

namespace Subsystem.Vom
{
    public enum VomFormat
    {
        Bf16,
        Half,
        Float32,
        Raw32,
        Int64,
        Bytes
    }

    public class Owner
    {
        public void Terminate() {}
    }

    public class Handle
    {
        public int Id { get; set; }
        public VomFormat Format { get; set; }
        public int ByteCount { get; set; }
        public IntPtr Resource { get; set; }
    }

    public class Fence
    {
        public void Signal(ulong value) {}
        public void Wait(ulong value) {}
    }

    public class Vom
    {
        private static readonly System.Collections.Generic.Dictionary<int, IntPtr> _allocated = new();
        private static int _nextId = 1;

        public static Owner CreateOwner(string name) => new Owner();

        public static Handle Alloc(Owner owner, int byteCount, VomFormat format, string type, bool withFence, string subdir, string name)
        {
            var h = new Handle { Format = format, ByteCount = byteCount };
            lock (_allocated)
            {
                h.Id = _nextId++;
                h.Resource = System.Runtime.InteropServices.Marshal.AllocHGlobal(byteCount);
                unsafe
                {
                    byte* p = (byte*)h.Resource;
                    for (int i = 0; i < byteCount; i++) p[i] = 0;
                }
                _allocated[h.Id] = h.Resource;
            }
            return h;
        }

        public static Fence GetFence(Owner owner, int handleId) => new Fence();

        public static void Close(Owner owner, int handleId)
        {
            lock (_allocated)
            {
                if (_allocated.TryGetValue(handleId, out var ptr))
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
                    _allocated.Remove(handleId);
                }
            }
        }
    }
}

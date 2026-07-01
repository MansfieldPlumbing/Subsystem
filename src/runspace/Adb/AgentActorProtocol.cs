using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Subsystem.Vom;

namespace Subsystem.Adb
{
    // The VOM-based in-process channel. Communicates by copying float arrays directly
    // to aligned native memory and signaling a monotonic CpuFence (zero-copy, zero-GC, zero-serialize).
    public class VomChannel
    {
        public Handle RegionHandle { get; }
        public CpuFence Fence { get; }
        public unsafe float* FloatPtr => (float*)RegionHandle.Resource;
        public int CapacityBytes => RegionHandle.ByteCount;

        public VomChannel(Handle handle, CpuFence fence)
        {
            RegionHandle = handle;
            Fence = fence;
        }

        // Writes a token activation packet into the 256-byte aligned native region.
        public unsafe void Write(long tokenId, int layerIndex, string expert, float[] data)
        {
            // Offset 0: TokenId (8 bytes)
            long* headerLong = (long*)FloatPtr;
            headerLong[0] = tokenId;

            // Offset 8: LayerIndex (4 bytes), DataLength (4 bytes)
            int* headerInt = (int*)(FloatPtr + 2); // 8 bytes offset
            headerInt[0] = layerIndex;
            headerInt[1] = data.Length;

            // Offset 16: Expert name (null-terminated ASCII)
            byte* expertBytes = (byte*)(FloatPtr + 4); // 16 bytes offset
            byte[] nameBytes = Encoding.ASCII.GetBytes(expert);
            int nameLen = Math.Min(nameBytes.Length, 63);
            Marshal.Copy(nameBytes, 0, (nint)expertBytes, nameLen);
            expertBytes[nameLen] = 0; // null terminator

            // Offset 256 (64 float stride): Raw activations payload
            float* dataPtr = FloatPtr + 64;
            Marshal.Copy(data, 0, (nint)dataPtr, data.Length);
        }

        // Reads the packet back from the native region.
        public unsafe (long tokenId, int layerIndex, string expert, float[] data) Read()
        {
            long* headerLong = (long*)FloatPtr;
            long tokenId = headerLong[0];

            int* headerInt = (int*)(FloatPtr + 2);
            int layerIndex = headerInt[0];
            int len = headerInt[1];

            byte* expertBytes = (byte*)(FloatPtr + 4);
            int nameLen = 0;
            while (expertBytes[nameLen] != 0 && nameLen < 64) nameLen++;
            byte[] nameBytes = new byte[nameLen];
            Marshal.Copy((nint)expertBytes, nameBytes, 0, nameLen);
            string expert = Encoding.ASCII.GetString(nameBytes);

            float[] data = new float[len];
            float* dataPtr = FloatPtr + 64;
            Marshal.Copy((nint)dataPtr, data, 0, len);

            return (tokenId, layerIndex, expert, data);
        }
    }

    // Connection representation for an in-process VOM actor.
    public class VomActorConnection
    {
        public string Name { get; }
        public string[] Experts { get; }
        public long FreeMemory { get; }
        public VomChannel Input { get; }
        public VomChannel Output { get; }
        public Owner DeviceOwner { get; }
        public ulong CurrentPhase { get; set; } = 0;

        public VomActorConnection(string name, string[] experts, long freeMemory, Owner owner, VomChannel input, VomChannel output)
        {
            Name = name;
            Experts = experts;
            FreeMemory = freeMemory;
            DeviceOwner = owner;
            Input = input;
            Output = output;
        }
    }

    // Registry and lifecycle manager of the attached device set.
    public class InProcessDeviceSet : IDisposable
    {
        private readonly ConcurrentDictionary<string, VomActorConnection> _devices = new();
        private readonly List<Owner> _createdOwners = new();

        public ICollection<VomActorConnection> Devices => _devices.Values;

        public VomActorConnection CreateDevice(string name, string[] experts, long freeMemory)
        {
            // Create VOM Owner to host the device's shared communication handles
            var owner = Vom.Vom.CreateOwner($"\\Actor\\{name}");
            _createdOwners.Add(owner);

            // Allocate input and output regions with monotonic CpuFences
            var hInput = Vom.Vom.Alloc(owner, 1024 * 1024, type: "VomRegion", withFence: true, name: "Input");
            var hOutput = Vom.Vom.Alloc(owner, 1024 * 1024, type: "VomRegion", withFence: true, name: "Output");

            var fInput = (CpuFence)Vom.Vom.GetFence(owner, hInput.Id)!;
            var fOutput = (CpuFence)Vom.Vom.GetFence(owner, hOutput.Id)!;

            var inputChannel = new VomChannel(hInput, fInput);
            var outputChannel = new VomChannel(hOutput, fOutput);

            var conn = new VomActorConnection(name, experts, freeMemory, owner, inputChannel, outputChannel);
            _devices[name] = conn;

            // Trigger handshake formatting in DialoguePresenter
            DialoguePresenter.FormatInProcessHandshake(name, experts, freeMemory);

            return conn;
        }

        public VomActorConnection? GetDevice(string name)
        {
            return _devices.TryGetValue(name, out var d) ? d : null;
        }

        public void Dispose()
        {
            foreach (var owner in _createdOwners)
            {
                try { Vom.Vom.Terminate(owner); } catch { }
            }
            _createdOwners.Clear();
            _devices.Clear();
        }
    }

    // In-process device agent runner. Executes in a dedicated thread.
    public class InProcessDeviceAgent : IDisposable
    {
        private readonly VomActorConnection _conn;
        private readonly CancellationTokenSource _cts = new();

        public InProcessDeviceAgent(VomActorConnection conn)
        {
            _conn = conn;
        }

        public void Start()
        {
            Task.Run(() => AgentLoopAsync(_cts.Token));
        }

        private async Task AgentLoopAsync(CancellationToken ct)
        {
            ulong phase = 1;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // OS-scheduler park: wait for Host to write input and advance InputFence to 'phase'
                    _conn.Input.Fence.CpuWait(phase);

                    // Read input activations directly from VOM shared region
                    var (tokenId, layerIndex, expert, data) = _conn.Input.Read();

                    DialoguePresenter.FormatInProcessReceive(_conn.Name, tokenId, expert, _conn.Input.RegionHandle.Path);

                    // Simulate expert execution (e.g. scale weights by 0.5)
                    float[] outputData = new float[data.Length];
                    for (int i = 0; i < data.Length; i++)
                    {
                        outputData[i] = data[i] * 0.5f;
                    }

                    // Write output activations directly into Output VOM shared region
                    _conn.Output.Write(tokenId, layerIndex, expert, outputData);

                    DialoguePresenter.FormatInProcessSend(_conn.Name, tokenId, outputData.Length, _conn.Output.RegionHandle.Path);

                    // Doorbell signal: notify Host that output is ready for this phase
                    _conn.Output.Fence.Signal(phase);

                    phase++;
                }
                catch (Exception ex)
                {
                    DialoguePresenter.LogDisconnected(_conn.Name, $"Agent loop faulted: {ex.Message}");
                    break;
                }
            }
            await Task.CompletedTask;
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }

    // In-process MoE router.
    public class MoeDispatch
    {
        private readonly InProcessDeviceSet _deviceSet;

        public MoeDispatch(InProcessDeviceSet deviceSet)
        {
            _deviceSet = deviceSet;
        }

        public async Task<float[]> RouteTokenAsync(long tokenId, string expertName, float[] inputActivations, CancellationToken ct = default)
        {
            VomActorConnection? target = null;
            foreach (var device in _deviceSet.Devices)
            {
                if (Array.IndexOf(device.Experts, expertName) >= 0)
                {
                    target = device;
                    break;
                }
            }

            if (target == null)
            {
                throw new InvalidOperationException($"No active device hosts expert '{expertName}'");
            }

            // Increment local phase clock
            target.CurrentPhase++;
            ulong phase = target.CurrentPhase;

            DialoguePresenter.FormatHostRoute("Host", target.Name, tokenId, expertName);

            // Write input activations directly into the device's Input VOM shared region
            target.Input.Write(tokenId, 0, expertName, inputActivations);

            // Doorbell signal: notify device agent to start execution
            target.Input.Fence.Signal(phase);

            // OS-scheduler park: wait for device to signal OutputFence reaching 'phase'
            await Task.Run(() => target.Output.Fence.CpuWait(phase), ct);

            // Read output activations directly from device's Output VOM shared region
            var (_, _, _, outputData) = target.Output.Read();

            DialoguePresenter.LogActivationReturned(expertName, tokenId, outputData.Length);

            return outputData;
        }

        // Route a batch of tokens concurrently and wait for all of them using Fence.WaitAll.
        public async Task<List<float[]>> RouteBatchAsync(List<(long tokenId, string expertName, float[] input)> batch, CancellationToken ct = default)
        {
            var targets = new List<VomActorConnection>();
            var targetPhases = new List<ulong>();
            var fences = new List<Fence>();
            
            foreach (var item in batch)
            {
                VomActorConnection? device = null;
                foreach (var d in _deviceSet.Devices)
                {
                    if (Array.IndexOf(d.Experts, item.expertName) >= 0)
                    {
                        device = d;
                        break;
                    }
                }
                if (device == null)
                    throw new InvalidOperationException($"No active device hosts expert '{item.expertName}'");

                device.CurrentPhase++;
                ulong phase = device.CurrentPhase;

                DialoguePresenter.FormatHostRoute("Host", device.Name, item.tokenId, item.expertName);

                device.Input.Write(item.tokenId, 0, item.expertName, item.input);
                device.Input.Fence.Signal(phase);

                targets.Add(device);
                targetPhases.Add(phase);
                fences.Add(device.Output.Fence);
            }

            // OS-scheduler park: WaitAll fences reach targetPhases concurrently
            await Task.Run(() => {
                Fence.WaitAll(fences.ToArray(), targetPhases.ToArray());
            }, ct);

            var results = new List<float[]>();
            for (int i = 0; i < batch.Count; i++)
            {
                var (_, _, expertName, outputData) = targets[i].Output.Read();
                DialoguePresenter.LogActivationReturned(expertName, batch[i].tokenId, outputData.Length);
                results.Add(outputData);
            }

            return results;
        }

        // Route a batch of tokens and wait for at least 'n' of them to complete using Fence.WaitN.
        public async Task<int> RouteBatchQuorumAsync(List<(long tokenId, string expertName, float[] input)> batch, int n, CancellationToken ct = default)
        {
            var targets = new List<VomActorConnection>();
            var targetPhases = new List<ulong>();
            var fences = new List<Fence>();
            
            foreach (var item in batch)
            {
                VomActorConnection? device = null;
                foreach (var d in _deviceSet.Devices)
                {
                    if (Array.IndexOf(d.Experts, item.expertName) >= 0)
                    {
                        device = d;
                        break;
                    }
                }
                if (device == null)
                    throw new InvalidOperationException($"No active device hosts expert '{item.expertName}'");

                device.CurrentPhase++;
                ulong phase = device.CurrentPhase;

                DialoguePresenter.FormatHostRoute("Host", device.Name, item.tokenId, item.expertName);

                device.Input.Write(item.tokenId, 0, item.expertName, item.input);
                device.Input.Fence.Signal(phase);

                targets.Add(device);
                targetPhases.Add(phase);
                fences.Add(device.Output.Fence);
            }

            // OS-scheduler park: WaitN fences reach targetPhases concurrently
            int readyCount = 0;
            await Task.Run(() => {
                readyCount = Fence.WaitN(fences.ToArray(), targetPhases.ToArray(), n);
            }, ct);

            return readyCount;
        }
    }

    public static class AgentActorHost
    {
        public static int Run(string[] args)
        {
            Console.WriteLine("=== Subsystem P2P In-Process VOM Agent-Actor Host ===");
            using var deviceSet = new InProcessDeviceSet();
            var orchestrator = new MoeDispatch(deviceSet);

            // Spin up the 4 device actors completely in-process (symmetrical peer-head architecture)
            var dev1 = deviceSet.CreateDevice("Galaxy-S23", new[] { "expert_0", "expert_1" }, 6400000000L);
            var dev2 = deviceSet.CreateDevice("Razr+", new[] { "expert_2", "expert_3" }, 4000000000L);
            var dev3 = deviceSet.CreateDevice("OnePlus", new[] { "expert_4", "expert_5" }, 8000000000L);
            var dev4 = deviceSet.CreateDevice("moto-edge", new[] { "expert_6", "expert_7" }, 3000000000L);

            using var agent1 = new InProcessDeviceAgent(dev1);
            using var agent2 = new InProcessDeviceAgent(dev2);
            using var agent3 = new InProcessDeviceAgent(dev3);
            using var agent4 = new InProcessDeviceAgent(dev4);

            agent1.Start();
            agent2.Start();
            agent3.Start();
            agent4.Start();

            // Simple interactive loop to send test tokens
            long nextTokenId = 100;
            var quitCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!quitCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(4000);

                    long id = nextTokenId++;
                    string expert = $"expert_{(id % 8)}"; // expert_0 to expert_7
                    float[] input = new float[1536];
                    for (int i = 0; i < input.Length; i++) input[i] = (float)(id + i);

                    try
                    {
                        var output = await orchestrator.RouteTokenAsync(id, expert, input, quitCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Host] Routing error: {ex.Message}");
                    }
                }
            });

            Console.WriteLine("Press Enter to quit.");
            Console.ReadLine();
            quitCts.Cancel();

            agent1.Stop();
            agent2.Stop();
            agent3.Stop();
            agent4.Stop();

            return 0;
        }
    }

    public static class AgentActorDevice
    {
        public static int Run(string[] args)
        {
            Console.WriteLine("Running in-process. Symmetrical peer actor is executed via 'ss actor'.");
            return 0;
        }
    }
}

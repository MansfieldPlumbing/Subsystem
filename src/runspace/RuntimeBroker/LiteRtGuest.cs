using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;

namespace Subsystem;

// LiteRtGuest — the guest-mount adapter for the LiteRT-LM foreign engine (the GUEST door a boundary-
// crossing engine is mounted through: construct, BringUp, register as a VOM object, cache). Rebuilt after
// RuntimeBroker/Broker were retired as a zero-truth middleman — this holds no wrapper: Runtime already
// exposes everything a caller needs (StreamTurnAsync, IsAlive, BackendName, OpenSideConversation), so the
// cached object IS the Runtime.
//
// §3: the engine is an EXECUTIVE OBJECT, not a static (SS003). This type holds no engine reference; every
// access resolves the active model's registry record to a namespace path and acquires through the handle
// table — a dispatch-against-disposed-engine condition is unrepresentable rather than handled.
public static class LiteRtGuest
{
    public const string OwnerPath = "\\Agent\\Guest\\LiteRt";

    // The system prompt is a registry CONTRACT (\Capability\Prompt, seeded from shell/prompts.json),
    // resolved live — never a C# const.
    private const string PromptPath = "\\Capability\\Prompt\\broker";
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static Subsystem.Vom.Owner EngineOwner =>
        Subsystem.Vom.Vom.CreateOwner(OwnerPath, maxBytes: 8L * 1024 * 1024 * 1024);

    private static string EnginePath(string unitId) => $"{OwnerPath}\\Engine\\{unitId}";

    private static string ResolveSystemPrompt(Context ctx)
    {
        try
        {
            var rec = Subsystem.Cm.Cm.Get(PromptPath);
            if (rec?.ManifestJson == null) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(rec.ManifestJson);
            var s = doc.RootElement.TryGetProperty("systemInstruction", out var v) ? (v.GetString() ?? "") : "";
            // Thinking mode is OFF by default (AgentSettings.UseThinking) — strip the leading <|think|>
            // toggle so the model answers directly instead of reasoning unbounded on the slow CPU rung.
            if (!Subsystem.AgentSettings.UseThinking(ctx) && s.StartsWith("<|think|>"))
                s = s.Substring("<|think|>".Length).TrimStart('\n');
            return s;
        }
        catch (Exception ex) { Dg.Warn("litert", ex); return ""; }
    }

    // Resolve the active unit's engine through the handle table. False when no engine object exists
    // or the registered object is not serviceable. Never constructs.
    private static bool TryAcquire(Context ctx, out Runtime runtime)
    {
        runtime = null!;
        try
        {
            var spec = ModelCatalog.Active(ctx);
            if (Subsystem.Vom.Vom.TryGetByPath(EngineOwner, EnginePath(spec.Id), out var h) &&
                GCHandle.FromIntPtr(h.Resource).Target is Runtime r)
            {
                runtime = r;
                return true;
            }
        }
        catch (Exception ex) { Dg.Warn("engine", ex); }
        return false;
    }

    // Telemetry surface (Dg.Snapshot / the state texture): reads through the handle table.
    public static bool IsReady
    {
        get { try { return TryAcquire(Android.App.Application.Context, out var r) && r.IsAlive; } catch (Exception ex) { Dg.Warn("litert", ex); return false; } }
    }

    public static string? BackendName
    {
        get { try { return TryAcquire(Android.App.Application.Context, out var r) ? r.BackendName : null; } catch (Exception ex) { Dg.Warn("litert", ex); return null; } }
    }

    // Acquire the active unit's serviceable engine, constructing it under admission control and
    // verification when absent. Throws RbFaultException (typed) on bring-up failure after demoting the
    // unit's registry record (§5).
    public static async Task<Runtime> GetAsync(Context ctx, Func<string, Task>? report = null, CancellationToken ct = default)
    {
        if (TryAcquire(ctx, out var live) && live.IsAlive) return live;

        await _gate.WaitAsync(ct);
        try
        {
            var spec = ModelCatalog.Active(ctx);
            var owner = EngineOwner;
            var path = EnginePath(spec.Id);

            // Re-check under the gate; rundown a registered-but-unserviceable object first.
            if (Subsystem.Vom.Vom.TryGetByPath(owner, path, out var h) &&
                GCHandle.FromIntPtr(h.Resource).Target is Runtime again)
            {
                if (again.IsAlive) return again;
                Subsystem.Vom.Vom.Close(owner, path);
                Dg.Log("engine", $"RUNDOWN {spec.Id}: unserviceable engine object reclaimed before rebuild");
            }

            var file = await ModelCatalog.EnsureAsync(ctx, spec, report ?? (_ => Task.CompletedTask), ct);
            Runtime runtime = new LiteRtRuntime(file, spec.Id, ResolveSystemPrompt(ctx), ctx.CacheDir?.AbsolutePath, sampler: spec.Sampler);

            // Bring-up + verification BEFORE publication into the namespace. Off the caller's
            // synchronization context — engine init is ~10 s of native work.
            var fault = await Task.Run(() => runtime.BringUp(), ct);
            if (fault != null)
            {
                try { runtime.Dispose(); } catch (Exception ex) { Dg.Warn("engine", ex); }
                ModelCatalog.Demote(ctx, spec.Id, fault);
                throw new RbFaultException(fault);
            }

            Subsystem.Vom.Vom.Register(owner, "Engine", runtime,
                onReclaim: () => { try { runtime.Dispose(); } catch (Exception ex) { Dg.Warn("engine", ex); } },
                subdir: "Engine", name: spec.Id);
            Dg.Log("engine", $"PUBLISH {spec.Id} on {runtime.BackendName} -> {path}");
            return runtime;
        }
        finally { _gate.Release(); }
    }

    // Rundown of every engine object (model switch, trim response, teardown). Reclaim closes the
    // native engines; weights/KV are released before any successor loads. In-flight turns finish
    // against their own acquired reference.
    public static void Reset()
    {
        var (n, bytes) = Subsystem.Vom.Vom.DropPrefix(OwnerPath + "\\Engine");
        if (n > 0) Dg.Log("engine", $"RUNDOWN engines: {n} handle(s) reclaimed");
    }
}

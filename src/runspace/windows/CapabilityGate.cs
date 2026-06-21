using System;
using System.Text.Json;
using Subsystem.Cm;

namespace Subsystem.Windows;

// CapabilityGate — the shared, fail-closed token gate for the host-windows producers/consumers
// (ss surface, ss mirror, ss view). Seeds \Capability\<kind>\<name> DEFAULT-DENY, optionally records an
// audited grant, then runs Subsystem.Cm.AccessCheck. One door for every privileged ss verb, so "safety
// from the start" is not re-implemented per feature. Authority is possession of the granted capability;
// this decides whether the operation runs at all.
internal static class CapabilityGate
{
    // Returns true if allowed. On deny: prints the reason + how to grant, returns false. capPath is the
    // \Capability\<kind>\<name> path so the caller can read the granted record's integrity (e.g. for SDDL).
    public static bool Allow(string kind, string name, bool grant, out string capPath)
    {
        capPath = $"\\Capability\\{kind}\\{name}";
        if (Cm.Cm.Get(capPath) is null)
        {
            Cm.Cm.Register(new CapabilityRecord
            {
                Path = capPath, Name = name, Type = "Mount", Owner = "\\System",
                Integrity = "User", StartType = "manual", Enabled = false,
                ManifestJson = JsonSerializer.Serialize(new
                {
                    version = 1, kind = kind.ToLowerInvariant(), id = name,
                    note = $"{kind} capability. Off by default (default-deny); grant to allow.",
                }),
            });
        }

        if (grant)
        {
            Cm.Cm.Set(capPath, enabled: true, startType: null);
            Console.WriteLine($"granted (audited) {capPath}");
        }

        var res = AccessCheck.Resolve(Caller.Local(), capPath);
        if (!res.Granted)
        {
            Console.Error.WriteLine($"DENIED — {res.Reason}");
            Console.Error.WriteLine($"  grant it:  ss Set-Capability -Path '{capPath}' -Enabled $true   (or add --grant)");
            return false;
        }
        return true;
    }
}

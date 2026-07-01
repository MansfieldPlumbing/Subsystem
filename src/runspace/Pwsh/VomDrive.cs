using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using VomKernel = Subsystem.Vom.Vom;   // the kernel CLASS — bare `Vom` here binds to the namespace, not the class

namespace Subsystem.Pwsh;

// vom: — a READ-ONLY PowerShell drive that projects the live VOM handle table as a navigable namespace.
// This is invariant 3 made tangible (the registry projects the namespace) and invariant 4 (the presenter
// holds nothing): every listing is recomputed fresh from Vom.GetOwners(), the provider keeps NO state.
// Leaves carry descriptor METADATA only — the raw native pointer (Handle.Resource) never crosses the
// boundary. Lets `Get-ChildItem vom:\Sessions` walk the running kernel like a filesystem.
[CmdletProvider("Vom", ProviderCapabilities.None)]
public sealed class VomDrive : NavigationCmdletProvider
{
    protected override Collection<PSDriveInfo> InitializeDefaultDrives() => new()
    {
        new PSDriveInfo("vom", ProviderInfo, "",
            "The live VOM namespace — read-only projection of the kernel handle table.", null),
    };

    protected override bool IsValidPath(string path) => true;

    protected override bool ItemExists(string path)
    {
        var v = ToVom(path);
        return v.Length == 0 || Project(out _).Contains(v);
    }

    protected override bool IsItemContainer(string path)
    {
        var v = ToVom(path);
        if (v.Length == 0) return true;
        var nodes = Project(out var children);
        return nodes.Contains(v) && children.ContainsKey(v);
    }

    protected override bool HasChildItems(string path)
    {
        Project(out var children);
        return children.TryGetValue(ToVom(path), out var kids) && kids.Count > 0;
    }

    protected override void GetItem(string path)
    {
        var v = ToVom(path);
        Project(out var children);
        WriteItemObject(NodeFor(v, children), path, children.ContainsKey(v));
    }

    protected override void GetChildItems(string path, bool recurse)
    {
        Project(out var children);
        EmitChildren(path, children, recurse);
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        Project(out var children);
        if (!children.TryGetValue(ToVom(path), out var kids)) return;
        foreach (var c in kids.OrderBy(LeafName, StringComparer.OrdinalIgnoreCase))
            WriteItemObject(LeafName(c), c, children.ContainsKey(c));
    }

    // --- the projection (held nowhere; rebuilt from the kernel each call) ---

    private void EmitChildren(string path, Dictionary<string, List<string>> children, bool recurse)
    {
        if (!children.TryGetValue(ToVom(path), out var kids)) return;
        foreach (var c in kids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            WriteItemObject(NodeFor(c, children), c, children.ContainsKey(c));
            if (recurse) EmitChildren(c, children, true);
        }
    }

    // Build the live node set + parent->children map from the kernel. A node is a VOM path with a leading
    // backslash; the synthetic root is "" and its children are the first path segments (\Sessions, \Device…).
    private static HashSet<string> Project(out Dictionary<string, List<string>> children)
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in VomKernel.GetOwners())
        {
            AddPath(nodes, o.Path);
            foreach (var hp in o.PathToId.Keys) AddPath(nodes, hp);
        }
        children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
        {
            var parent = ParentOf(n);
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<string>();
            list.Add(n);
        }
        return nodes;
    }

    private static void AddPath(HashSet<string> nodes, string path)
    {
        var p = path;
        while (p.Length > 0 && nodes.Add(p)) p = ParentOf(p);   // walk to root; stop where ancestors already exist
    }

    private static string ParentOf(string vomPath)
    {
        int cut = vomPath.LastIndexOf('\\');
        return cut <= 0 ? "" : vomPath.Substring(0, cut);
    }

    private static string LeafName(string vomPath)
    {
        int cut = vomPath.LastIndexOf('\\');
        return cut < 0 ? vomPath : vomPath.Substring(cut + 1);
    }

    // Normalize any PowerShell path form (vom:\X, \X, X, X/Y) to a VOM path: "" for the root, else "\…".
    private static string ToVom(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('/', '\\');
        int colon = p.IndexOf(':');
        if (colon >= 0) p = p.Substring(colon + 1);     // drop a "vom:" drive qualifier
        p = p.Trim('\\');
        return p.Length == 0 ? "" : "\\" + p;
    }

    // Project one node into a display record: owner stats for an owner, descriptor metadata for a handle
    // leaf, a namespace marker for an interior segment. The raw Resource pointer is deliberately omitted.
    private static VomNode NodeFor(string v, Dictionary<string, List<string>> children)
    {
        int ChildCount(string p) => children.TryGetValue(p, out var k) ? k.Count : 0;

        if (v.Length == 0)
        {
            var (owners, handles, bytes) = VomKernel.Totals();
            return new VomNode { Path = "\\", Name = "\\", Kind = "root", Handles = handles, Bytes = bytes, Children = owners };
        }
        var owner = VomKernel.GetOwner(v);
        if (owner != null)
            return new VomNode
            {
                Path = v, Name = LeafName(v), Kind = "owner",
                Handles = owner.PathToId.Count,
                Bytes = System.Threading.Interlocked.Read(ref owner.CurrentBytes),
                MaxBytes = owner.MaxBytes,
                Cancelled = owner.Cts.IsCancellationRequested,
                Children = ChildCount(v),
            };
        foreach (var o in VomKernel.GetOwners())
            if (o.PathToId.ContainsKey(v) && VomKernel.TryGetByPath(o, v, out var h))
                return new VomNode { Path = v, Name = LeafName(v), Kind = "handle", Type = h.Type, Bytes = h.ByteCount, Format = h.Format.ToString() };
        return new VomNode { Path = v, Name = LeafName(v), Kind = "namespace", Children = ChildCount(v) };
    }
}

// The display record for a vom: node — the projection's leaf shape, NOT the kernel's handle. Carries
// addressing + stats only; the native Resource pointer never appears here.
public sealed class VomNode
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";       // root | namespace | owner | handle
    public long Handles { get; set; }
    public long Bytes { get; set; }
    public long MaxBytes { get; set; }
    public bool Cancelled { get; set; }
    public string? Type { get; set; }           // handle descriptor type
    public string? Format { get; set; }         // handle format
    public int Children { get; set; }
}

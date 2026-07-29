using System;

namespace Subsystem.Remedy;

// One request — an Incident (defect) or Change (enhancement/RFC) row.
public sealed class RequestRecord
{
    public long          Id           { get; set; }
    public string        Kind         { get; set; } = "";   // Incident | Change
    public string        Category     { get; set; } = "";
    public string        Summary      { get; set; } = "";
    public string[]      RelatedFiles { get; set; } = Array.Empty<string>();
    public int           Severity     { get; set; }
    public string        Status       { get; set; } = "";   // Open | Closed
    public long          Created      { get; set; }         // unix epoch seconds
    public long          Closed       { get; set; }         // 0 == still open
    public string        Disposition  { get; set; } = "";   // how it was closed
    public EosLogEntry[] EosLog       { get; set; } = Array.Empty<EosLogEntry>();   // append-only end-of-session history

    // Remedy reference — a PROJECTION over the dense integer PK (Remedy-{Type}Request-id), never stored.
    public string        Ref          => RequestHive.GetRef(Kind, Id);
}

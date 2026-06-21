using System;
using System.Management.Automation;
using Subsystem.Cm;

namespace Subsystem.Windows;

// Remedy-IncidentRequest / Remedy-ChangeRequest / Get-Request / Close-Request / Add-EosLog — the PowerShell
// front door to the registry's Request table (Cm's durable plane), named after Remedy ITSM. Remedy IS the verb
// for opening a Request: you REMEDY an incident or a change — creation is remedying. The record type is the
// NOUN (Remedy-{Type}Request, matching the Ref projection), so there is no -Type flag. Call-center lifecycle:
// a Request is REMEDIED open, worked (Add-EosLog appends append-only end-of-session entries), queried, and
// CLOSED with a mandatory disposition. Thin bindings: parse params, call the approved-verb RequestHive methods,
// emit the row as an object — so `Get-Request | ConvertTo-Json` renders flat JSON on demand while the truth
// stays dense in SQLite. Requests live in the binary and are queried from it; there is no workorder file.

// Remedy-IncidentRequest — open an Incident (a defect). Verb Remedy, noun IncidentRequest (the Remedy record).
[Cmdlet("Remedy", "IncidentRequest")]
[OutputType(typeof(RequestRecord))]
public sealed class RemedyIncidentRequestCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Summary { get; set; } = "";

    [Parameter(Mandatory = true)]
    [Alias("c")]
    [ValidateSet("Vom", "Cm", "Dg", "Pp", "Rb", "Rs", "Pwsh", "Device", "gate", "build", "shell", "agent")]
    public string Category { get; set; } = "";

    [Parameter]
    [Alias("f")]
    public string[] RelatedFiles { get; set; } = Array.Empty<string>();

    [Parameter]
    [ValidateRange(1, 3)]
    public int Severity { get; set; } = 2;

    protected override void ProcessRecord()
        => WriteObject(RequestHive.Create("Incident", Category, Summary, RelatedFiles, Severity));
}

// Remedy-ChangeRequest — open a Change (an enhancement / RFC). Verb Remedy, noun ChangeRequest.
[Cmdlet("Remedy", "ChangeRequest")]
[OutputType(typeof(RequestRecord))]
public sealed class RemedyChangeRequestCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Summary { get; set; } = "";

    [Parameter(Mandatory = true)]
    [Alias("c")]
    [ValidateSet("Vom", "Cm", "Dg", "Pp", "Rb", "Rs", "Pwsh", "Device", "gate", "build", "shell", "agent")]
    public string Category { get; set; } = "";

    [Parameter]
    [Alias("f")]
    public string[] RelatedFiles { get; set; } = Array.Empty<string>();

    [Parameter]
    [ValidateRange(1, 3)]
    public int Severity { get; set; } = 2;

    protected override void ProcessRecord()
        => WriteObject(RequestHive.Create("Change", Category, Summary, RelatedFiles, Severity));
}

[Cmdlet(VerbsCommon.Get, "Request")]
[OutputType(typeof(RequestRecord))]
public sealed class GetRequestCmdlet : PSCmdlet
{
    // By number: `Get-Request 7`.
    [Parameter(Position = 0)]
    public long Id { get; set; }

    [Parameter]
    [Alias("t")]
    [ValidateSet("Incident", "Change")]
    public string? Type { get; set; }

    [Parameter]
    [Alias("c")]
    [ValidateSet("Vom", "Cm", "Dg", "Pp", "Rb", "Rs", "Pwsh", "Device", "gate", "build", "shell", "agent")]
    public string? Category { get; set; }

    [Parameter]
    [ValidateSet("Open", "Closed")]
    public string? Status { get; set; }

    // Keyword: case-insensitive substring over summary + disposition.
    [Parameter]
    [Alias("q")]
    public string? Search { get; set; }

    // Date window on when the Request was opened.
    [Parameter] public DateTime? Since { get; set; }
    [Parameter] public DateTime? Before { get; set; }

    // Default view is the open queue; -All (or an explicit -Status / -Id) widens it.
    [Parameter]
    public SwitchParameter All { get; set; }

    protected override void ProcessRecord()
    {
        bool byId = MyInvocation.BoundParameters.ContainsKey(nameof(Id));
        long? id = byId ? Id : (long?)null;
        string? status = Status;
        if (status == null && !All.IsPresent && !byId) status = "Open";   // default = the open queue
        long since  = Since.HasValue  ? new DateTimeOffset(Since.Value.ToUniversalTime()).ToUnixTimeSeconds()  : 0;
        long before = Before.HasValue ? new DateTimeOffset(Before.Value.ToUniversalTime()).ToUnixTimeSeconds() : 0;
        foreach (var t in RequestHive.Query(Type, Category, status, id, Search, since, before)) WriteObject(t);
    }
}

// Close-Request — the terminal disposition. The disposition is MANDATORY and non-empty: a Request cannot be
// closed without saying how it was handled.
[Cmdlet(VerbsCommon.Close, "Request")]
[OutputType(typeof(RequestRecord))]
public sealed class CloseRequestCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public long Id { get; set; }

    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    [Alias("d")]
    public string Disposition { get; set; } = "";

    protected override void ProcessRecord()
    {
        var t = RequestHive.Close(Id, Disposition);
        if (t == null)
            WriteError(new ErrorRecord(new ItemNotFoundException($"no Request #{Id}"),
                "RequestNotFound", ErrorCategory.ObjectNotFound, Id));
        else
            WriteObject(t);
    }
}

// Add-EosLog — append an End-Of-Session entry to a Request's append-only EOS log: a disposition (how the
// session left it) plus the session report. The durable record that lets the raw chat be thrown away.
// Append-only; entries are never edited or deleted.
[Cmdlet(VerbsCommon.Add, "EosLog")]
[OutputType(typeof(EosLogEntry))]
public sealed class AddEosLogCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public long Id { get; set; }

    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    [Alias("d")]
    public string Disposition { get; set; } = "";

    [Parameter(Mandatory = true, Position = 2)]
    [ValidateNotNullOrEmpty]
    [Alias("b")]
    public string Body { get; set; } = "";

    protected override void ProcessRecord()
    {
        var e = RequestHive.WriteEosLog(Id, Disposition, Body);
        if (e == null)
            WriteError(new ErrorRecord(new ItemNotFoundException($"no Request #{Id}"),
                "RequestNotFound", ErrorCategory.ObjectNotFound, Id));
        else
            WriteObject(e);
    }
}

using System;
using System.Management.Automation;
using Subsystem.Cm;

namespace Subsystem.Windows;

// Open-Ticket / Get-Ticket / Close-Ticket — the PowerShell front door to the registry's ticket table
// (Cm's durable plane). Call-center lifecycle: a ticket is OPENED, queried, and CLOSED with a mandatory
// disposition. Thin bindings: parse params, call the approved-verb TicketTable methods, emit the row as an
// object — so `Get-Ticket | ConvertTo-Json` renders flat JSON on demand while the truth stays dense in
// SQLite. Tickets live in the binary and are queried from it; there is no workorder file.

[Cmdlet(VerbsCommon.Open, "Ticket")]
[OutputType(typeof(TicketRecord))]
public sealed class OpenTicketCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Summary { get; set; } = "";

    [Parameter(Mandatory = true)]
    [Alias("t")]
    [ValidateSet("TroubleTicket", "FeatureRequest")]
    public string Type { get; set; } = "";

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
        => WriteObject(TicketTable.Create(Type, Category, Summary, RelatedFiles, Severity));
}

[Cmdlet(VerbsCommon.Get, "Ticket")]
[OutputType(typeof(TicketRecord))]
public sealed class GetTicketCmdlet : PSCmdlet
{
    // By number: `Get-Ticket 7`.
    [Parameter(Position = 0)]
    public long Id { get; set; }

    [Parameter]
    [Alias("t")]
    [ValidateSet("TroubleTicket", "FeatureRequest")]
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

    // Date window on when the ticket was opened.
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
        foreach (var t in TicketTable.Query(Type, Category, status, id, Search, since, before)) WriteObject(t);
    }
}

// Close-Ticket — the terminal disposition. The disposition is MANDATORY and non-empty: a ticket cannot be
// closed without saying how it was handled.
[Cmdlet(VerbsCommon.Close, "Ticket")]
[OutputType(typeof(TicketRecord))]
public sealed class CloseTicketCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public long Id { get; set; }

    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    [Alias("d")]
    public string Disposition { get; set; } = "";

    protected override void ProcessRecord()
    {
        var t = TicketTable.Close(Id, Disposition);
        if (t == null)
            WriteError(new ErrorRecord(new ItemNotFoundException($"no ticket #{Id}"),
                "TicketNotFound", ErrorCategory.ObjectNotFound, Id));
        else
            WriteObject(t);
    }
}

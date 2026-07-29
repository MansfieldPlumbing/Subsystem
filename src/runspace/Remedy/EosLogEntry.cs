using System;

namespace Subsystem.Remedy;

// One EOS-log entry — an append-only End-Of-Session record.
public sealed class EosLogEntry
{
    public long   Request     { get; set; }
    public long   Noted       { get; set; }   // unix epoch seconds
    public string Disposition { get; set; } = "";   // how the session left it
    public string Body        { get; set; } = "";   // the end-of-session report
}

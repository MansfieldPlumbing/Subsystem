using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace Subsystem.Windows;

// A minimal PSHost wired to the real Console. Without a host, an SDK runspace silently DROPS the host
// streams — Write-Host / Write-Information / Write-Warning / Write-Verbose vanish (friction F8: a -File
// script ending in Write-Host produced nothing). With this host + Out-Default, ss renders exactly like
// the pwsh console — success objects (formatted), Write-Host, warnings — in order, and `exit` can't
// swallow it (Out-Default writes DURING the run, not in a terminal end-block). SetShouldExit also
// captures `exit N`, so ss propagates the script's exit code.
internal sealed class ConsoleHost : PSHost
{
    private readonly ConsoleHostUserInterface _ui = new();
    private readonly Guid _id = Guid.NewGuid();
    public int? ExitCode { get; private set; }

    public override string Name => "ss";
    public override Version Version => new(1, 0);
    public override Guid InstanceId => _id;
    public override PSHostUserInterface UI => _ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void SetShouldExit(int exitCode) => ExitCode = exitCode;
    public override void EnterNestedPrompt() => throw new NotSupportedException("ss is non-interactive");
    public override void ExitNestedPrompt() => throw new NotSupportedException("ss is non-interactive");
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class ConsoleHostUserInterface : PSHostUserInterface
{
    private readonly ConsoleHostRawUserInterface _raw = new();
    public override PSHostRawUserInterface RawUI => _raw;

    public override void Write(string value) => Console.Out.Write(value);
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
    {
        var fg = Console.ForegroundColor; var bg = Console.BackgroundColor;
        try { Console.ForegroundColor = foregroundColor; Console.BackgroundColor = backgroundColor; Console.Out.Write(value); }
        catch { Console.Out.Write(value); }
        finally { try { Console.ForegroundColor = fg; Console.BackgroundColor = bg; } catch { } }
    }
    public override void WriteLine(string value) => Console.Out.WriteLine(value);
    public override void WriteErrorLine(string value) => Console.Error.WriteLine(value);
    public override void WriteDebugLine(string message) => Console.Error.WriteLine("DEBUG: " + message);
    public override void WriteVerboseLine(string message) => Console.Error.WriteLine("VERBOSE: " + message);
    public override void WriteWarningLine(string message) => Console.Error.WriteLine("WARNING: " + message);
    public override void WriteProgress(long sourceId, ProgressRecord record) { }

    // Non-interactive: there is no console to read from in the agent/one-shot path. Refuse rather than hang.
    public override string ReadLine() => string.Empty;
    public override SecureString ReadLineAsSecureString() => new();
    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => new();
    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName)
        => throw new NotSupportedException("ss is non-interactive");
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        => throw new NotSupportedException("ss is non-interactive");
}

internal sealed class ConsoleHostRawUserInterface : PSHostRawUserInterface
{
    private readonly int _w;
    public ConsoleHostRawUserInterface()
    {
        int w = 120;
        try { if (Console.WindowWidth > 1) w = Console.WindowWidth; } catch { }
        _w = w;
    }

    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
    public override Coordinates CursorPosition { get; set; }
    public override Coordinates WindowPosition { get; set; }
    public override int CursorSize { get; set; } = 1;
    public override Size BufferSize { get => new(_w, 9999); set { } }
    public override Size WindowSize { get => new(_w, 50); set { } }
    public override Size MaxWindowSize => new(_w, 50);
    public override Size MaxPhysicalWindowSize => new(_w, 50);
    public override string WindowTitle { get; set; } = "ss";
    public override bool KeyAvailable => false;
    public override KeyInfo ReadKey(ReadKeyOptions options) => default;
    public override void FlushInputBuffer() { }
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];
    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
}

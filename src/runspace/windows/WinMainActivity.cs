namespace Subsystem;

// Seam: shared core files resolve the app's private data dir by probing
// Subsystem.MainActivity.Instance?.FilesDir?.AbsolutePath (Cm.Ensure does — the registry hive lives
// there). On the Windows head that probe answers %LocalAppData%\Subsystem through this type instead
// of an Android Activity. Same name + member shape as the device host so the shared files compile
// unchanged; grow members only when the compiler demands.
public sealed class MainActivity
{
    public static MainActivity? Instance { get; } = new();

    public AppFilesDir FilesDir { get; } = new();

    public sealed class AppFilesDir
    {
        public string AbsolutePath { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subsystem");
    }
}

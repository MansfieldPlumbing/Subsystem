using System.IO;

namespace Subsystem.Cm.EnvironmentVariables;

// Db — product-agnostic resolver for database locations under the resolved Home directory.
public static class Db
{
    public static string Dir => Path.Combine(Home.Value, "db");
    public static string Config => Path.Combine(Dir, "configuration.db");
    public static string Requests => Path.Combine(Dir, "requests.db");

    static Db()
    {
        try { Directory.CreateDirectory(Dir); }
        catch (Exception ex) { Dg.Warn("db", $"Directory.CreateDirectory failed for db dir '{Dir}': {ex.Message}"); }
    }
}

using System;

namespace Subsystem.Adb
{
    public static class DialoguePresenter
    {
        private static readonly object ConsoleLock = new object();

        public static void FormatInProcessHandshake(string sender, string[] experts, long freeMemory)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Green;
                string expertsList = string.Join(", ", experts);
                Console.WriteLine($"[{now}] [{sender}] \"MoE Expert set [{expertsList}] loaded. Ready for prefill. Free memory: {FormatBytes(freeMemory)}.\"");
                Console.ResetColor();
            }
        }

        public static void FormatHostRoute(string sender, string recipient, long tokenId, string expertName)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[{now}] [{sender}] \"Routing token {tokenId} to {recipient} for expert {expertName}... [VOM region \\Actor\\{recipient}\\Objects\\Input]\"");
                Console.ResetColor();
            }
        }

        public static void FormatInProcessReceive(string sender, long tokenId, string expert, string path)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{now}] [{sender}] \"Received token {tokenId} for expert {expert}. Computing... [VOM region {path}]\"");
                Console.ResetColor();
            }
        }

        public static void FormatInProcessSend(string sender, long tokenId, int length, string path)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[{now}] [{sender}] \"Returned activations for token {tokenId} (length: {length} floats) [VOM region {path}]\"");
                Console.ResetColor();
            }
        }

        public static void LogActivationReturned(string expert, long tokenId, int length)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[{now}] [Host] \"Successfully aggregated activations for token {tokenId} from expert {expert} ({length} floats)\"");
                Console.ResetColor();
            }
        }

        public static void LogDisconnected(string id, string message)
        {
            lock (ConsoleLock)
            {
                var now = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{now}] [System] \"Actor {id} faulted: {message}\"");
                Console.ResetColor();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return $"{(double)bytes / (1024 * 1024 * 1024):F2} GB";
            }
            if (bytes >= 1024L * 1024L)
            {
                return $"{(double)bytes / (1024 * 1024):F2} MB";
            }
            return $"{bytes} B";
        }
    }
}

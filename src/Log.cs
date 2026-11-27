namespace BatteryTray;

/// <summary>
/// %LOCALAPPDATA%\BatteryTray\app.log: start, exit and errors, always on (kept below 256 KB).
/// raw.log: raw device replies, only when started with --log.
/// </summary>
public static class Log
{
    public static bool Enabled;
    private const long MaxAppLog = 256 * 1024;
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray");
    private static readonly object Gate = new();

    public static void App(string message) => Append("app.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}", MaxAppLog);

    public static void Raw(string source, byte[] data)
    {
        if (Enabled) Append("raw.log", $"{DateTime.Now:HH:mm:ss} {source}: {Convert.ToHexString(data).ToLowerInvariant()}", 0);
    }

    private static void Append(string name, string line, long maxBytes)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, name);
                // Start a new file instead of growing without limit; one old copy is kept.
                if (maxBytes > 0 && File.Exists(path) && new FileInfo(path).Length > maxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception) { /* logging must never stop the app */ }
    }
}

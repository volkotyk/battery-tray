namespace BatteryTray;

/// <summary>Raw replies in %LOCALAPPDATA%\BatteryTray\raw.log, only when started with --log.</summary>
public static class Log
{
    public static bool Enabled;
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray", "raw.log");
    private static readonly object Gate = new();

    public static void Raw(string source, byte[] data)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss} {source}: {Convert.ToHexString(data).ToLowerInvariant()}{Environment.NewLine}");
        }
    }
}

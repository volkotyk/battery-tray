namespace BatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Drop admin rights before the single-instance mutex, so the child can take it.
        if (!args.Contains(Elevation.RestrictedArg) && Elevation.IsAdmin()
            && Elevation.RelaunchRestricted(args))
            return;

        using var single = new Mutex(true, @"Local\BatteryTray", out bool first);
        if (!first) return;

        Log.Enabled = args.Contains("--log");
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

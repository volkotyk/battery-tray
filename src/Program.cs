namespace BatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Log.App($"start: {Environment.ProcessPath} args=[{string.Join(' ', args)}] admin={Elevation.IsAdmin()}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.App($"crash: {e.ExceptionObject}");

        // Drop admin rights before the single-instance mutex, so the child can take it.
        if (!args.Contains(Elevation.RestrictedArg) && Elevation.IsAdmin())
        {
            if (Elevation.RelaunchRestricted(args)) { Log.App("restarted with a normal-user token"); return; }
            Log.App("could not drop admin rights, running as is");
        }

        using var single = new Mutex(true, @"Local\BatteryTray", out bool first);
        if (!first) { Log.App("another instance runs, exit"); return; }

        Log.Enabled = args.Contains("--log");
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.App($"UI error: {e.Exception}");
        Application.Run(new TrayApp());
        Log.App("exit");
    }
}

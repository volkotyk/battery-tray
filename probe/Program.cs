using BatteryTray;

// Reads both devices with the app's own readers. "watch <minutes>" repeats every 10 s.
// Raw replies go to %LOCALAPPDATA%\BatteryTray\raw.log.
Log.Enabled = true;
int minutes = args.Length == 2 && args[0] == "watch" ? int.Parse(args[1]) : 0;
DateTime end = DateTime.Now.AddMinutes(minutes);
do
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {new ArctisSource().Read()} | {new RazerSource().Read()}");
    if (minutes > 0) Thread.Sleep(10_000);
} while (DateTime.Now < end);

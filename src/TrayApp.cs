using Microsoft.Win32;

namespace BatteryTray;

/// <summary>One tray icon per device; a background loop polls all devices.</summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "BatteryTray";
    private const string TrayKey = @"Control Panel\NotifyIconSettings";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30), SleepGrace = TimeSpan.FromMinutes(2);

    private sealed class Item(IBatterySource source, NotifyIcon icon)
    {
        public IBatterySource Source { get; } = source;
        public NotifyIcon Icon { get; } = icon;
        public BatteryState State { get; set; } = BatteryState.Missing("reading...");
        public LowBatteryAlert Alert { get; } = new();
        public int? LastPercent { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private readonly Item[] _items;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 500 };
    private int _phase;

    public TrayApp()
    {
        _items = new IBatterySource[] { new ArctisSource(), new RazerSource() }
            .Select(s => new Item(s, new NotifyIcon { Text = s.Name, ContextMenuStrip = BuildMenu(), Visible = true }))
            .ToArray();
        // The first control (ContextMenuStrip) installs the WinForms context on this thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        foreach (Item item in _items) Show(item);

        _anim.Tick += (_, _) =>
        {
            _phase++;
            foreach (Item item in _items.Where(i => i.State.Charging)) Show(item);
        };
        _anim.Start();
        try { Toast.Register(); } catch (Exception) { /* balloon fallback still works */ }
        _ = Task.Run(PollLoop);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, (_, _) => _wake.Release());
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart() };
        autostart.Click += (_, _) => { SetAutostart(!IsAutostart()); autostart.Checked = IsAutostart(); };
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => autostart.Checked = IsAutostart();
        return menu;
    }

    private async Task PollLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (Item item in _items)
            {
                BatteryState state;
                try { state = item.Source.Read(); }
                catch (Exception e) { state = BatteryState.Missing(e.Message); }
                _ui.Post(_ =>
                {
                    item.State = state;
                    if (state.Percent is int p) { item.LastPercent = p; item.LastSeen = DateTime.Now; }
                    Show(item);
                    Notify(item);
                }, null);
            }
            // Explorer creates the tray entries only after the icons appear, so check on every cycle.
            PromoteTrayIcons();
            try { await _wake.WaitAsync(Interval, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Show(Item item)
    {
        BatteryState s = item.State;
        // The mouse stops answering a few seconds after the last movement: keep the last level
        // for a while, and show Zz only for a longer silence. Alerts still use the real state.
        if (s.Presence == Presence.Asleep && item.LastPercent is int idle && DateTime.Now - item.LastSeen < SleepGrace)
            s = new BatteryState(idle, false, $"idle, last reply {item.LastSeen:HH:mm}");
        string text = s.Percent is int p
            ? $"{item.Source.Name}: {p}%{(s.Charging ? ", charging" : "")}{(s.Detail.Length > 0 ? $" ({s.Detail})" : "")}"
            : $"{item.Source.Name}: {s.Detail}"
              + (item.LastPercent is int last ? $", last {last}% at {item.LastSeen:HH:mm}" : "");
        item.Icon.Text = text.Length > 127 ? text[..127] : text;

        Icon? old = item.Icon.Icon;
        item.Icon.Icon = IconPainter.Draw(s, item.Source.IsHeadset, _phase, item.LastPercent);
        if (old is not null) IconPainter.Destroy(old);
    }

    private static void Notify(Item item)
    {
        int tier = item.Alert.Update(item.State);
        if (tier == 0) return;
        string title = tier == 2 ? $"{item.Source.Name}: battery critical" : $"{item.Source.Name}: battery low";
        string body = $"{item.State.Percent}% left. Connect the charger.";
        if (!Toast.Show(title, body, SaveToastImage(item)))
            item.Icon.ShowBalloonTip(10_000, title, body, tier == 2 ? ToolTipIcon.Error : ToolTipIcon.Warning);
    }

    /// <summary>The tray icon at 4x, as the toast logo. Null if the file can not be written.</summary>
    private static string? SaveToastImage(Item item)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, item.Source.IsHeadset ? "toast-headset.png" : "toast-mouse.png");
            Icon icon = IconPainter.Draw(item.State, item.Source.IsHeadset, size: 64);
            try { using Bitmap bmp = icon.ToBitmap(); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            finally { IconPainter.Destroy(icon); }
            return path;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Shows our icons on the taskbar instead of the overflow menu. Only entries without
    /// IsPromoted are changed, so a later "off" in Settings > Taskbar stays as the user set it.
    /// </summary>
    private static void PromoteTrayIcons()
    {
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(TrayKey);
            if (root is null) return;
            foreach (string name in root.GetSubKeyNames())
            {
                using RegistryKey? k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue("ExecutablePath") is string path
                    && string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                    && k.GetValue("IsPromoted") is null)
                    k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception) { /* cosmetic only */ }
    }

    protected override void ExitThreadCore()
    {
        _stop.Cancel();
        _anim.Dispose();
        foreach (Item item in _items) { item.Icon.Visible = false; item.Icon.Dispose(); }
        base.ExitThreadCore();
    }

    private static bool IsAutostart()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    private static void SetAutostart(bool on)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunValue, false);
    }
}

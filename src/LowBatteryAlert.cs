namespace BatteryTray;

/// <summary>
/// Decides when to warn: once at 20 %, once more at 10 %. The Arctis percent comes from the
/// battery voltage and jumps by a few points, so the alert re-arms only above 25 % or on the charger.
/// </summary>
public sealed class LowBatteryAlert
{
    public const int Warning = 20, Critical = 10, Rearm = 25;
    private int _notifiedTier;

    /// <returns>1 = low, 2 = critical, 0 = no new notification.</returns>
    public int Update(BatteryState s)
    {
        if (s.Percent is not int p) return 0; // asleep or off: keep the state
        if (s.Charging || p > Rearm) { _notifiedTier = 0; return 0; }

        int tier = p <= Critical ? 2 : p <= Warning ? 1 : 0;
        if (tier <= _notifiedTier) return 0;
        _notifiedTier = tier;
        return tier;
    }
}

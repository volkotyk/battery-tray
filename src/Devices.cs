namespace BatteryTray;

/// <summary>Why a device gives no level. Online always comes with a Percent.</summary>
public enum Presence { Online, Asleep, Off, Missing }

public sealed record BatteryState(int? Percent, bool Charging, string Detail = "", Presence Presence = Presence.Online)
{
    public static BatteryState Missing(string detail) => new(null, false, detail, Presence.Missing);
}

public interface IBatterySource
{
    string Name { get; }
    bool IsHeadset { get; }
    BatteryState Read();
}

/// <summary>
/// SteelSeries Arctis 7P (1038:12D5), as in HeadsetControl steelseries_arctis_1.hpp:
/// output report {06 12} on interface 3, 0xff43:0x0202; reply {06 12 status battery ...},
/// status 0x01 = headset off, battery is already a percent, byte 4 = power state.
/// </summary>
public sealed class ArctisSource : IBatterySource
{
    public string Name => "Arctis 7P";
    public bool IsHeadset => true;

    public BatteryState Read()
    {
        HidInfo? info = HidDevice.Enumerate(0x1038, 0x12D5)
            .FirstOrDefault(i => i.Interface == 3 && i.UsagePage == 0xff43 && i.Usage == 0x0202);
        if (info is null) return BatteryState.Missing("dongle not found");

        using HidDevice? dev = HidDevice.Open(info);
        if (dev is null) return BatteryState.Missing("dongle busy");

        dev.FlushInput();
        var req = new byte[info.OutputLen];
        req[0] = 0x06; req[1] = 0x12;
        if (!dev.Write(req)) return BatteryState.Missing("write failed");

        // Other reports (chatmix, volume) can arrive first; wait for the matching reply.
        DateTime until = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < until)
        {
            byte[]? r = dev.Read(500);
            if (r is null || r.Length < 4 || r[0] != 0x06 || r[1] != 0x12) continue;
            Log.Raw(Name, r);
            // Only on/off is known (HeadsetControl); the inactivity timer also gives "off".
            if (r[2] == 0x01) return new BatteryState(null, false, "turned off", Presence.Off);
            // Byte 4, found on this unit (not in HeadsetControl): 0x03 on battery, 0x01 on the cable.
            string detail = r.Length > 4 && r[4] is not (0x01 or 0x03) ? $"power state 0x{r[4]:x2}" : "";
            return new BatteryState(Math.Clamp((int)r[3], 0, 100), r.Length > 4 && r[4] == 0x01, detail);
        }
        return BatteryState.Missing("no reply");
    }
}

/// <summary>
/// Razer Basilisk Ultimate: 0x0086 = cable/dock, 0x0088 = HyperSpeed receiver.
/// Feature report on interface 0 (openrazer razer_report, transaction 0x1F):
/// class 0x07, id 0x80 = battery 0-255, id 0x84 = charging 0/1, value in arguments[1].
/// </summary>
public sealed class RazerSource : IBatterySource
{
    private const byte Transaction = 0x1F, StatusBusy = 0x01, StatusOk = 0x02, StatusTimeout = 0x04;
    private static readonly ushort[] Pids = { 0x0086, 0x0088 };

    public string Name => "Basilisk Ultimate";
    public bool IsHeadset => false;

    public BatteryState Read()
    {
        bool found = false, timedOut = false;
        foreach (ushort pid in Pids)
        foreach (HidInfo info in HidDevice.Enumerate(0x1532, pid).Where(i => i.Interface == 0 && i.FeatureLen >= 91))
        {
            found = true;
            using HidDevice? dev = HidDevice.Open(info);
            if (dev is null) continue;
            byte? level = Query(dev, 0x80, out byte status);
            // The receiver (or the dock without the mouse) answers "timeout" when the mouse does not reply.
            if (level is null) { timedOut |= status == StatusTimeout; continue; }
            byte? charging = Query(dev, 0x84, out _);
            return new BatteryState((int)Math.Round(level.Value * 100 / 255.0), charging == 1,
                pid == 0x0086 ? "cable/dock" : "wireless");
        }
        // A sleeping and a switched-off mouse give the same 0x04 from the receiver.
        if (timedOut) return new BatteryState(null, false, "asleep or off", Presence.Asleep);
        return BatteryState.Missing(found ? "no reply" : "receiver not found");
    }

    /// <param name="status">Last Razer status byte seen, 0 when no reply matched.</param>
    private static byte? Query(HidDevice dev, byte commandId, out byte status)
    {
        status = 0;
        // Byte 0 is the HID report ID (0); the 90-byte Razer report follows.
        var req = new byte[91];
        req[2] = Transaction; req[6] = 0x02; req[7] = 0x07; req[8] = commandId;
        byte crc = 0;
        for (int k = 3; k < 89; k++) crc ^= req[k];
        req[89] = crc;

        // OpenRGB may talk to the same device: accept only the final reply to this command.
        for (int send = 0; send < 3; send++)
        {
            if (!dev.SetFeature(req)) return null;
            for (int k = 0; k < 25; k++)
            {
                Thread.Sleep(4);
                var r = new byte[91];
                if (!dev.GetFeature(r) || r[2] != Transaction || r[7] != 0x07 || r[8] != commandId) continue;
                status = r[1];
                if (r[1] == 0x00 || r[1] == StatusBusy) continue;
                if (r[1] != StatusOk) return null;
                Log.Raw("Razer " + commandId.ToString("x2"), r[..16]);
                return r[10];
            }
        }
        return null;
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

/// <summary>One HID top-level collection, as Windows exposes it.</summary>
public sealed record HidInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage,
    int InputLen, int OutputLen, int FeatureLen)
{
    /// <summary>USB interface number from the path ("mi_03"), or -1.</summary>
    public int Interface
    {
        get
        {
            int i = Path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
            return i >= 0 && int.TryParse(Path.AsSpan(i + 3, 2), System.Globalization.NumberStyles.HexNumber, null, out int mi) ? mi : -1;
        }
    }
}

/// <summary>Minimal Win32 HID access: enumerate, feature reports, output/input reports.</summary>
public sealed class HidDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    public HidInfo Info { get; }

    private HidDevice(SafeFileHandle handle, HidInfo info) { _handle = handle; Info = info; }

    public static List<HidInfo> Enumerate(ushort vid, ushort pid)
    {
        var result = new List<HidInfo>();
        Native.HidD_GetHidGuid(out Guid guid);
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                string? path = GetPath(set, ref ifData);
                if (path is null || !path.Contains($"vid_{vid:x4}&pid_{pid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                HidInfo? info = Query(path);
                if (info is not null && info.Vid == vid && info.Pid == pid) result.Add(info);
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static string? GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            return Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(buf + 4) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static HidInfo? Query(string path)
    {
        using SafeFileHandle h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(h, ref attr)) return null;
        if (!Native.HidD_GetPreparsedData(h, out IntPtr pp)) return null;
        try
        {
            if (Native.HidP_GetCaps(pp, out Native.HIDP_CAPS caps) != Native.HIDP_STATUS_SUCCESS) return null;
            return new HidInfo(path, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally { Native.HidD_FreePreparsedData(pp); }
    }

    /// <summary>Opens read/write; falls back to no access (enough for feature reports on mouse collections).</summary>
    public static HidDevice? Open(HidInfo info)
    {
        const uint share = Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE;
        SafeFileHandle h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, share, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            h = Native.CreateFile(info.Path, 0, share, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new HidDevice(h, info);
    }

    public bool SetFeature(byte[] report) => Native.HidD_SetFeature(_handle, report, report.Length);

    public bool GetFeature(byte[] report) => Native.HidD_GetFeature(_handle, report, report.Length);

    /// <summary>Drops queued input reports so the next read returns a fresh answer.</summary>
    public void FlushInput() => Native.HidD_FlushQueue(_handle);

    /// <summary>Writes one output report (byte 0 is the report ID).</summary>
    public bool Write(byte[] report)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        GCHandle pin = GCHandle.Alloc(report, GCHandleType.Pinned);
        try
        {
            if (Native.WriteFile(_handle, pin.AddrOfPinnedObject(), report.Length, out _, ref ov)) return true;
            if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
            if (ev.WaitOne(1000)) return Native.GetOverlappedResult(_handle, ref ov, out _, false);
            Native.CancelIoEx(_handle, ref ov);
            Native.GetOverlappedResult(_handle, ref ov, out _, true);
            return false;
        }
        finally { pin.Free(); }
    }

    /// <summary>Reads one input report, or returns null after the timeout.</summary>
    public byte[]? Read(int timeoutMs)
    {
        byte[] buf = new byte[Info.InputLen];
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        // The buffer must stay pinned while the overlapped read is pending.
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (!Native.ReadFile(_handle, pin.AddrOfPinnedObject(), buf.Length, out _, ref ov)
                && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return null;
            if (!ev.WaitOne(timeoutMs))
            {
                Native.CancelIoEx(_handle, ref ov);
                Native.GetOverlappedResult(_handle, ref ov, out _, true);
                return null;
            }
            return Native.GetOverlappedResult(_handle, ref ov, out int n, false) && n > 0 ? buf[..n] : null;
        }
        finally { pin.Free(); }
    }

    public void Dispose() => _handle.Dispose();

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997, HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, ref NativeOverlapped ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, ref NativeOverlapped ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, ref NativeOverlapped ov, out int n, bool wait);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, ref NativeOverlapped ov);
    }
}

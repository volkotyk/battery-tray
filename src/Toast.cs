using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// Windows toast for an unpackaged app: AUMID registered under HKCU\Software\Classes\AppUserModelId,
/// WinRT called through raw vtables (the Windows SDK projection would add ~25 MB to the exe).
/// Legacy NotifyIcon balloons do not reach the notification centre on this PC.
/// </summary>
public static unsafe class Toast
{
    private const string Aumid = "BatteryTray";

    private static readonly Guid IID_IXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    private static readonly Guid IID_IXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IID_IToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IID_IToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");

    public static void Register()
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid);
        k.SetValue("DisplayName", "BatteryTray");
    }

    /// <summary>Returns false when any WinRT call fails, so the caller can fall back to a balloon.</summary>
    public static bool Show(string title, string body, string? imagePath)
    {
        string image = imagePath is null ? "" :
            $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(new Uri(imagePath).AbsoluteUri)}\"/>";
        string xml = "<toast><visual><binding template=\"ToastGeneric\">" +
            $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>{image}" +
            "</binding></visual></toast>";

        IntPtr doc = 0, docIo = 0, docXml = 0, factory = 0, toast = 0, statics = 0, notifier = 0;
        try
        {
            using (var cls = new HString("Windows.Data.Xml.Dom.XmlDocument"))
                if (RoActivateInstance(cls, out doc) < 0) return false;
            if (Marshal.QueryInterface(doc, in IID_IXmlDocumentIO, out docIo) < 0) return false;
            using (var s = new HString(xml))
                if (Call(docIo, 6, s) < 0) return false; // IXmlDocumentIO.LoadXml
            if (Marshal.QueryInterface(doc, in IID_IXmlDocument, out docXml) < 0) return false;

            using (var cls = new HString("Windows.UI.Notifications.ToastNotification"))
                if (RoGetActivationFactory(cls, in IID_IToastNotificationFactory, out factory) < 0) return false;
            if (Call(factory, 6, docXml, &toast) < 0) return false; // CreateToastNotification

            using (var cls = new HString("Windows.UI.Notifications.ToastNotificationManager"))
                if (RoGetActivationFactory(cls, in IID_IToastNotificationManagerStatics, out statics) < 0) return false;
            using (var id = new HString(Aumid))
                if (Call(statics, 7, id, &notifier) < 0) return false; // CreateToastNotifierWithId

            return Call(notifier, 6, toast) >= 0; // IToastNotifier.Show
        }
        catch (Exception) { return false; }
        finally
        {
            foreach (IntPtr p in new[] { notifier, statics, toast, factory, docXml, docIo, doc })
                if (p != 0) Marshal.Release(p);
        }
    }

    private static int Call(IntPtr obj, int slot, IntPtr arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(*(void***)obj + slot)))(obj, arg);

    private static int Call(IntPtr obj, int slot, IntPtr arg, IntPtr* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(*(void***)obj + slot)))(obj, arg, result);

    private sealed class HString : IDisposable
    {
        private readonly IntPtr _h;
        public HString(string s) => Marshal.ThrowExceptionForHR(WindowsCreateString(s, s.Length, out _h));
        public static implicit operator IntPtr(HString s) => s._h;
        public void Dispose() => WindowsDeleteString(_h);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
}

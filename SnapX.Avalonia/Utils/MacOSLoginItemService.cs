using System.Runtime.InteropServices;

namespace SnapX.Avalonia.Utils;

public enum LoginItemStatus { Unavailable = -1, NotRegistered, Enabled, RequiresApproval, NotFound }

public interface ILoginItemService
{
    bool CanManage { get; }
    LoginItemStatus GetStatus();
    void Register();
    void Unregister();
    void OpenSystemSettings();
}

// Kept independent of UI and persisted preferences: macOS is the source of truth.
public sealed class LoginItemController(ILoginItemService service)
{
    public bool ShouldOffer(bool alreadyAsked) => !alreadyAsked && service.CanManage &&
        service.GetStatus() is LoginItemStatus.NotRegistered or LoginItemStatus.NotFound;

    public LoginItemStatus ApplyChoice(bool optedIn)
    {
        if (!optedIn) return service.GetStatus();
        if (!service.CanManage) throw new InvalidOperationException("Move SnapX to the Applications folder before you enable launch at login.");
        var status = service.GetStatus();
        if (status is not (LoginItemStatus.Enabled or LoginItemStatus.RequiresApproval)) service.Register();
        return service.GetStatus();
    }
}

public sealed class MacOSLoginItemService : ILoginItemService
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<IntPtr> ServiceClass = new(() =>
    {
        // Retain the framework for the process lifetime: its ObjC classes stay registered.
        NativeLibrary.Load("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement");
        var cls = GetClass("SMAppService");
        return cls != IntPtr.Zero ? cls : throw new PlatformNotSupportedException("This version of macOS does not support login item services.");
    });

    public bool CanManage => OperatingSystem.IsMacOSVersionAtLeast(13) &&
        IsInstalledBundlePath(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static bool IsInstalledBundlePath(string baseDirectory, string userHome)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        if (directory.Name != "MacOS" || directory.Parent?.Name != "Contents" ||
            directory.Parent.Parent is not { } bundle || !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return false;
        string path = bundle.FullName;
        string userApplications = Path.Combine(Path.GetFullPath(userHome), "Applications") + Path.DirectorySeparatorChar;
        return path.StartsWith("/Applications/", StringComparison.Ordinal) || path.StartsWith(userApplications, StringComparison.Ordinal);
    }

    public LoginItemStatus GetStatus() => CanManage ? ReadNativeStatus() : LoginItemStatus.Unavailable;

    // Read-only diagnostic shared by the native ABI smoke test; never registers anything.
    public static LoginItemStatus ReadNativeStatus()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return LoginItemStatus.Unavailable;
        var value = SendInteger(MainService(), Selector("status"));
        return value is >= 0 and <= 3 ? (LoginItemStatus)value : LoginItemStatus.Unavailable;
    }

    public void Register() => ChangeRegistration("registerAndReturnError:");
    public void Unregister() => ChangeRegistration("unregisterAndReturnError:");
    public void OpenSystemSettings()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(13)) return;
        SendVoid(ServiceClass.Value, Selector("openSystemSettingsLoginItems"));
    }

    private void ChangeRegistration(string operation)
    {
        if (!CanManage) throw new InvalidOperationException("Install SnapX in the Applications folder before you change launch at login.");
        if (SendWithError(MainService(), Selector(operation), out var error) != 0) return;
        string message = "macOS could not change the login item. In System Settings, go to General, and then Login Items and Extensions.";
        if (error != IntPtr.Zero)
        {
            var description = SendPointer(error, Selector("localizedDescription"));
            var utf8 = SendPointer(description, Selector("UTF8String"));
            message = Marshal.PtrToStringUTF8(utf8) ?? message;
        }
        throw new InvalidOperationException(message);
    }

    private static IntPtr MainService()
    {
        var service = SendPointer(ServiceClass.Value, Selector("mainAppService"));
        return service != IntPtr.Zero ? service : throw new InvalidOperationException("macOS could not identify this application bundle.");
    }

    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")] private static extern IntPtr GetClass(string name);
    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern nint SendInteger(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte SendWithError(IntPtr receiver, IntPtr selector, out IntPtr error);
}

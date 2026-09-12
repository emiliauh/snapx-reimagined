using SnapX.Avalonia.Utils;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
foreach (var (path, expected) in new[]
{
    ("/Applications/SnapX.app/Contents/MacOS/", true),
    ("/Users/test/Applications/SnapX.app/Contents/MacOS", true),
    ("/Applications/Utilities/SnapX.app/Contents/MacOS", true),
    ("/Volumes/SnapX Installer/SnapX.app/Contents/MacOS", false),
    ("/tmp/SnapX.app/Contents/MacOS", false),
    ("/ApplicationsFake/SnapX.app/Contents/MacOS", false),
    ("/Applications/SnapX.app/Contents", false),
    ("/Users/other/Applications/SnapX.app/Contents/MacOS", false),
    ("/Applications/../tmp/SnapX.app/Contents/MacOS", false)
}) Check(MacOSLoginItemService.IsInstalledBundlePath(path, "/Users/test") == expected, "Wrong install path eligibility: " + path);

var service = new FakeService();
var controller = new LoginItemController(service);
Check(controller.ShouldOffer(false), "First installed launch should offer an opt-in.");
Check(!controller.ShouldOffer(true), "Dismissed prompt must not repeat.");
controller.ApplyChoice(false);
Check(service.RegisterCalls == 0, "Declining registered the application.");
Check(controller.ApplyChoice(true) == LoginItemStatus.Enabled && service.RegisterCalls == 1, "Explicit opt-in did not register.");
controller.ApplyChoice(true);
Check(service.RegisterCalls == 1, "Already enabled login item was registered again.");
Check(!controller.ShouldOffer(false), "Already enabled login item should not prompt.");
service.Status = LoginItemStatus.RequiresApproval;
Check(controller.ApplyChoice(true) == LoginItemStatus.RequiresApproval && service.RegisterCalls == 1, "Pending approval was automatically re-registered.");
service.Status = LoginItemStatus.NotRegistered; // Simulate removal in System Settings.
Check(!controller.ShouldOffer(true) && service.RegisterCalls == 1, "Removal should not trigger automatic registration or another prompt.");
service.Status = LoginItemStatus.NotFound; // macOS has never seen a fresh installed app.
Check(controller.ShouldOffer(false), "Fresh app with NotFound status must offer opt-in.");
Check(!controller.ShouldOffer(true), "Dismissed fresh-app prompt must not repeat.");
controller.ApplyChoice(false);
Check(service.RegisterCalls == 1, "Declining a fresh app registered it.");
Check(controller.ApplyChoice(true) == LoginItemStatus.Enabled && service.RegisterCalls == 2, "Fresh app could not register after explicit opt-in.");
service.CanManage = false;
Check(!controller.ShouldOffer(false), "Uninstalled bundle should not prompt.");
try { controller.ApplyChoice(true); throw new Exception("Uninstalled app was registered."); }
catch (InvalidOperationException) { checks++; }
Check(service.RegisterCalls == 2, "Unsupported registration reached native service.");
if (args.Contains("--native-status"))
{
    var status = MacOSLoginItemService.ReadNativeStatus();
    Check(Enum.IsDefined(status), "Native ABI returned an invalid status.");
    Console.WriteLine("Native read-only status: " + status);
}
Console.WriteLine($"PASS: {checks} startup consent/path/state checks; no real login items were changed.");

sealed class FakeService : ILoginItemService
{
    public bool CanManage { get; set; } = true;
    public LoginItemStatus Status { get; set; }
    public int RegisterCalls { get; private set; }
    public LoginItemStatus GetStatus() => Status;
    public void Register() { RegisterCalls++; Status = LoginItemStatus.Enabled; }
    public void Unregister() => Status = LoginItemStatus.NotRegistered;
    public void OpenSystemSettings() { }
}

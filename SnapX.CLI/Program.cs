using System.Reflection;
using SnapX.CLI;
using SnapX.Core.Capture;
using SnapX.Core.Job;
using SnapX.Core.Utils;

if (args.Length != 0 && (args[0] == "--version" || args[0] == "-v"))
{
    var informationalVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
    Console.WriteLine(informationalVersion);
    return;
}

var snapx = new SnapX.Core.SnapXL();
snapx.IdentifyAsCLI();
snapx.start(args);

var CLIManager = snapx.GetCLIManager();

await CLIManager.UseCommandLineArgs();

var version = Helpers.GetApplicationVersion();
if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    var changelog = new CLIChangelog(version);
    changelog.Display();
    var about = new CLIAbout();
    about.Show();

    Console.WriteLine();
    Console.WriteLine("SnapX.CLI is an empty project to dedicated to the developer feedback loop.");
    Console.WriteLine("It makes running SnapX's CLI faster than running Avalonia and it's more simple & universal.");
    Console.WriteLine("You can use ShareX's documentation found here. https://getsharex.com/docs/command-line-arguments to test SnapX.Core");
}
var sigintReceived = false;


Console.CancelKeyPress += (_, ea) =>
{
    if (sigintReceived) return;
    ea.Cancel = true;
    sigintReceived = true;
    Console.WriteLine("Received SIGINT (Ctrl+C)");
    snapx.shutdown();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!sigintReceived)
    {
        sigintReceived = true;
        Console.WriteLine("Received SIGTERM");
        snapx.shutdown();
    }
    else
    {
        Console.WriteLine("Received SIGTERM, ignoring it because already processed SIGINT");
    }
};
if (!sigintReceived)
{
    // A capture/upload job started by UseCommandLineArgs runs on its own
    // background thread. Exiting immediately here has cost real captures:
    // the process was observed to terminate before the background save
    // completed, leaving no file on disk. Give the pending work a bounded
    // window to finish before shutting down.
    await CaptureBase.WaitForActiveCaptureAsync();

    var waitTimeout = TimeSpan.FromSeconds(60);
    var waitStart = DateTime.UtcNow;
    while (TaskManager.IsBusy && DateTime.UtcNow - waitStart < waitTimeout)
    {
        await Task.Delay(100);
    }

    if (TaskManager.IsBusy)
    {
        Console.WriteLine("A background task is still running after 60 seconds; shutting down anyway.");
    }

    snapx.shutdown();
}

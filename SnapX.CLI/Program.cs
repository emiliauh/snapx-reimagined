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
    Console.WriteLine("SnapX.CLI provides command-line access to SnapX.Core.");
    Console.WriteLine("Use it to test SnapX.Core without the graphical application.");
    Console.WriteLine("For command-line instructions, read the ShareX documentation: https://getsharex.com/docs/command-line-arguments");
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
        Console.WriteLine("SnapX received SIGTERM after SIGINT. SnapX will ignore SIGTERM.");
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
        Console.WriteLine("A background task did not finish in 60 seconds. SnapX will close now.");
    }

    snapx.shutdown();
}

// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using SnapX.NativeMessagingHost;

if (args.Length == 0)
{
    Console.WriteLine("This executable is used to receive data from a browser addon and send it to SnapX.");
    return;
}

try
{
    var host = new NativeMessagingHost();
    var input = host.Read();

    if (!string.IsNullOrEmpty(input))
    {
        host.Write(input);
        var snapXPath = FindSnapX();

        var tempFilePath = GetTempFilePath("json");
        File.WriteAllText(tempFilePath, input, Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = snapXPath,
            Arguments = $"-NativeMessagingInput \"{tempFilePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) return;
        // Drain both pipes concurrently: a child filling stderr must not block
        // while the host waits for stdout to close.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Debug.WriteLine($"Output: {output}");
        if (process.ExitCode == 0) return;
        Console.Error.WriteLine($"Process exited with error code {process.ExitCode}");
        Console.Error.WriteLine($"Error output: {error}");
        Environment.ExitCode = process.ExitCode;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"{e.GetType()}: {e.Message}\n{e.StackTrace}");
    Environment.ExitCode = 1;
}

return;


static string FindSnapX(string? binary = null)
{
    var knownBinaryNames = new[]
    {
            "snapx-ui", // SnapX.Avalonia
            "snapx", // SnapX.CLI
            "SnapX" // Could literally be anything.
        };
    if (OperatingSystem.IsWindows()) knownBinaryNames = knownBinaryNames.Select(name => name + ".exe").ToArray();

    var path = Environment.GetEnvironmentVariable("PATH");

    if (!string.IsNullOrWhiteSpace(binary))
    {
        var foundBinary = FindBinaryInPath(binary, path);
        if (foundBinary != null)
            return foundBinary;

        // Check if the binary exists in the base directory
        var baseDirBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, binary);
        if (File.Exists(baseDirBinary))
            return baseDirBinary;
    }

    // If no binary is provided, search through the known binary names in the PATH and BaseDirectory
    foreach (var knownBinary in knownBinaryNames)
    {
        var foundBinary = FindBinaryInPath(knownBinary, path);
        if (foundBinary != null)
            return foundBinary;

        // Check if the known binary exists in the base directory
        var baseDirBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, knownBinary);
        if (File.Exists(baseDirBinary))
            return baseDirBinary;
    }

    // Return null if no binary is found
    Console.Error.WriteLine("SnapX was not found in PATH or BaseDirectory.");
    return string.Empty;
}

static string? FindBinaryInPath(string binaryName, string? path)
{
    // Split the PATH by the platform-specific path separator
    var pathEntries = path?.Split(Path.PathSeparator);

    // Search for the binary in each path entry
    return pathEntries?.Select(entry => Path.Combine(entry, binaryName)).FirstOrDefault(File.Exists);

    // Return null if the binary is not found in the PATH
}
static string GetTempFilePath(string extension)
{
    var tempFolder = Path.GetTempPath();
    Directory.CreateDirectory(tempFolder);
    var tempFilePath = Path.ChangeExtension(Path.Combine(tempFolder, Path.GetRandomFileName()), extension);
    File.Create(tempFilePath).Dispose();
    return tempFilePath;
}

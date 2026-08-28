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

        string? tempFilePath = null;
        try
        {
            tempFilePath = WritePrivateTempFile(input);

            var startInfo = new ProcessStartInfo
            {
                FileName = snapXPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NativeMessagingInput");
            startInfo.ArgumentList.Add(tempFilePath);

            using var process = Process.Start(startInfo);
            if (process == null) return;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Debug.WriteLine($"Output: {output}");
            if (process.ExitCode == 0) return;
            Console.Error.WriteLine($"Process exited with error code {process.ExitCode}");
            Console.Error.WriteLine($"Error output: {error}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"{e.GetType()}: {e.Message}\n{e.StackTrace}");
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

    if (!string.IsNullOrWhiteSpace(binary))
    {
        var baseDirBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, binary);
        if (File.Exists(baseDirBinary))
            return baseDirBinary;

        var foundBinary = FindBinaryInPath(binary, Environment.GetEnvironmentVariable("PATH"));
        if (foundBinary != null)
            return foundBinary;
    }

    // Prefer the installed peer binary. Falling back to PATH keeps development and
    // legacy layouts working without allowing PATH to override a packaged peer.
    foreach (var knownBinary in knownBinaryNames)
    {
        var baseDirBinary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, knownBinary);
        if (File.Exists(baseDirBinary))
            return baseDirBinary;

        var foundBinary = FindBinaryInPath(knownBinary, Environment.GetEnvironmentVariable("PATH"));
        if (foundBinary != null)
            return foundBinary;
    }

    // Return null if no binary is found
    Console.WriteLine("SnapX NOT found in PATH or BaseDirectory. Weewoo weewoo");
    return string.Empty;
}

static string? FindBinaryInPath(string binaryName, string? path)
{
    // Split the PATH by the platform-specific path separator
    var pathEntries = path?.Split(Path.PathSeparator);

    // Search for the binary in each path entry
    return pathEntries?
        .Where(entry => !string.IsNullOrWhiteSpace(entry))
        .Select(entry => Path.Combine(entry, binaryName))
        .FirstOrDefault(File.Exists);

    // Return null if the binary is not found in the PATH
}
static string WritePrivateTempFile(string input)
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"snapx-native-{Path.GetRandomFileName()}.json");
        try
        {
            using var stream = CreatePrivateTempStream(tempFilePath);

            using var writer = new StreamWriter(stream, new UTF8Encoding(false, true), 4096, leaveOpen: true);
            writer.Write(input);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return tempFilePath;
        }
        catch (IOException) when (attempt < 9)
        {
            // Extremely unlikely random-name collision; try a fresh path.
        }
    }

    throw new IOException("Could not create a private native-messaging temporary file.");
}

static FileStream CreatePrivateTempStream(string path)
{
    if (OperatingSystem.IsWindows())
    {
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    }

    return CreateUnixPrivateTempStream(path);
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static FileStream CreateUnixPrivateTempStream(string path) => new(path, new FileStreamOptions
{
    Mode = FileMode.CreateNew,
    Access = FileAccess.Write,
    Share = FileShare.None,
    BufferSize = 4096,
    Options = FileOptions.WriteThrough,
    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
});

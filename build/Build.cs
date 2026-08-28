using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DefaultNamespace;

public class Build(IBuildLogger Logger, ICommandRunner CommandRunner, IFileSystem FileSystem, BuildConfig config)
{
    private bool _hasLoggedInfo;

    public async Task ProcessBuildProject(
        string project)
    {
        LogBuildInfo();

        var index = Array.IndexOf(config.projectsToBuild, project);
        var assemblyName = config.knownAssemblyNames[index];
        var ridPart = $"-r {config.Runtime}";
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var isUnsupportedArch = arch is "s390x" or "ppc64le";
        var ridArg = OperatingSystem.IsLinux() && !isUnsupportedArch ? "" : ridPart;

        await CommandRunner.RunAsync("dotnet", $"publish \"{project}\" --configuration {config.Configuration} --nologo -o \"{Path.Combine(config.OutputDir, assemblyName)}\" {ridArg} {config.ExtraArgs}");

        if (OperatingSystem.IsLinux() && assemblyName == "snapx-ui")
        {
            await HandleWaylandOutlineCopy(config.RootDirectory, config.OutputDir, assemblyName);
        }

        if (project.Contains("NativeMessagingHost"))
        {
            await HandleNativeMessagingHost(assemblyName, config.OutputDir, config.LibDir, config.projectsToBuild.Where(p => !p.Contains("NativeMessagingHost")));
            await HandleRustLibCopy(config.RootDirectory, config.OutputDir);
        }
    }

    private void LogBuildInfo()
    {
        if (_hasLoggedInfo) return;
        Logger.Information($"Operating System: {RuntimeInformation.OSDescription}");
        Logger.Information($"SnapX Version: {config.SnapXVersion}");
        Logger.Information($"Architecture: {RuntimeInformation.OSArchitecture}");
        Logger.Information($"Runtime Identifier: {RuntimeInformation.RuntimeIdentifier}");
        _hasLoggedInfo = true;
    }

    private async Task HandleNativeMessagingHost(string assemblyName, string outputDir, string libDir, IEnumerable<string> otherProjects)
    {
        var finalAssemblyName = assemblyName;
        if (OperatingSystem.IsWindows()) finalAssemblyName += ".exe";
        var sourceNMHOutputPath = Path.Combine(outputDir, assemblyName, finalAssemblyName);

        foreach (var builtProject in otherProjects)
        {
            var builtAssemblyName = config.knownAssemblyNames[Array.IndexOf(config.projectsToBuild, builtProject)];
            FileSystem.FileCopy(sourceNMHOutputPath, Path.Combine(outputDir, builtAssemblyName, finalAssemblyName), overwrite: true);
        }

        FileSystem.DirectoryDelete(Path.Combine(outputDir, assemblyName), true);

        var manifestFiles = FileSystem.DirectoryGetFiles(outputDir, "host-manifest-*.json", SearchOption.AllDirectories);
        foreach (var manifestFile in manifestFiles)
        {
            var json = JsonNode.Parse(await FileSystem.FileReadAllTextAsync(manifestFile))?.AsObject();
            var NMHostPath = !OperatingSystem.IsWindows() ? Path.Join(libDir, "snapx", assemblyName) : null;

            if (string.IsNullOrWhiteSpace(NMHostPath))
            {
                Logger.Information($"Skipping {manifestFile} since NMHostPath was not provided");
                continue;
            }
            if (json is null) continue;
            json["path"] = NMHostPath;

            await FileSystem.FileWriteAllTextAsync(manifestFile, json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
    }

    private Task HandleRustLibCopy(string rootDirectory, string outputDir)
    {
        const string rustLib = "libsnapxrust.dylib";
        var sourcePath = Path.Combine(rootDirectory, "SnapX.Core", "ScreenCapture", "Rust", "target", "release", rustLib);

        if (!File.Exists(sourcePath)) return Task.CompletedTask;

        foreach (var dir in FileSystem.DirectoryGetDirectories(outputDir, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(dir, rustLib);
            FileSystem.FileCopy(sourcePath, destinationPath, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private async Task HandleWaylandOutlineCopy(string rootDirectory, string outputDir, string assemblyName)
    {
        const string nativeDir = "Native";
        string sourceDir = Path.Combine(rootDirectory, "SnapX.Avalonia", nativeDir);
        string cSrc = Path.Combine(sourceDir, "snapx-outline.c");
        string pickerSrc = Path.Combine(sourceDir, "snapx-picker.c");
        string layerCode = Path.Combine(sourceDir, "layer-shell-code.c");
        string xdgCode = Path.Combine(sourceDir, "xdg-shell-code.c");
        string relativePointerCode = Path.Combine(sourceDir, "relative-pointer-code.c");

        if (!File.Exists(pickerSrc) || !File.Exists(layerCode) || !File.Exists(xdgCode) ||
            !File.Exists(relativePointerCode))
        {
            Logger.Information($"Skipping snapx-picker: native sources not found in {sourceDir}");
        }
        else
        {
            string pickerOutputBinary = Path.Combine(outputDir, assemblyName, "snapx-picker");
            Directory.CreateDirectory(Path.GetDirectoryName(pickerOutputBinary)!);

            string pickerCflags = GetPkgConfigFlags("wayland-client");
            string pickerCommand = $"gcc \"{pickerSrc}\" \"{layerCode}\" \"{xdgCode}\" \"{relativePointerCode}\" -o \"{pickerOutputBinary}\" -I\"{sourceDir}\" {pickerCflags} -lm";
            Logger.Information($"Compiling snapx-picker: {pickerCommand}");
            try
            {
                await CommandRunner.RunAsync("bash", $"-c \"{pickerCommand}\"");
            }
            catch
            {
                Logger.Information("snapx-picker compile failed; native window-or-region selection will be unavailable.");
            }
        }

        if (!File.Exists(cSrc) || !File.Exists(layerCode) || !File.Exists(xdgCode) ||
            !File.Exists(relativePointerCode))
        {
            Logger.Information($"Skipping snapx-outline: native sources not found in {sourceDir}");
            return;
        }

        string outputBinary = Path.Combine(outputDir, assemblyName, "snapx-outline");
        Directory.CreateDirectory(Path.GetDirectoryName(outputBinary)!);

        string cflags = GetPkgConfigFlags("wayland-client");
        string pangoCflags = GetPkgConfigFlags("pangocairo");
        string cmd = $"gcc \"{cSrc}\" \"{layerCode}\" \"{xdgCode}\" \"{relativePointerCode}\" -o \"{outputBinary}\" -I\"{sourceDir}\" {cflags} {pangoCflags} -lm";
        Logger.Information($"Compiling snapx-outline: {cmd}");
        try
        {
            await CommandRunner.RunAsync("bash", $"-c \"{cmd}\"");
        }
        catch
        {
            Logger.Information("snapx-outline compile failed; Wayland outline will fall back to Avalonia windows.");
            throw new InvalidOperationException(
                "snapx-outline (recording controller) failed to compile; install the pangocairo/wayland development packages.");
        }
    }

    private static string GetPkgConfigFlags(string package)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = "pkg-config", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--cflags"); psi.ArgumentList.Add("--libs"); psi.ArgumentList.Add(package);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return "";
            string outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return outp.Trim();
        }
        catch { return ""; }
    }

}

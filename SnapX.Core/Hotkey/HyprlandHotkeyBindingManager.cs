// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SnapX.Core.Hotkey;

/// <summary>
/// Maintains the small SnapX-owned section of the compositor user bindings
/// file.
/// Hyprland launches a SnapX CLI action for these bindings, which works for
/// native Wayland clients even when a desktop portal accepts a shortcut but
/// does not deliver it to the application.
/// </summary>
public static class HyprlandHotkeyBindingManager
{
    private const string BeginMarker = "-- BEGIN SNAPX MANAGED HOTKEYS - DO NOT EDIT";
    private const string EndMarker = "-- END SNAPX MANAGED HOTKEYS";
    // A settings page can submit two Apply/Clear actions before the first
    // hyprctl reload returns. Serializing the entire read-modify-write cycle
    // prevents the later writer from silently discarding the earlier entry.
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    public static bool IsSupported => OperatingSystem.IsLinux() && IsHyprlandSession() &&
        File.Exists(GetBindingsPath());

    public static async Task<HyprlandHotkeySyncResult> ApplyAsync(
        HotkeySettings setting,
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Update(setting, remove: false, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    public static async Task<HyprlandHotkeySyncResult> ClearAsync(
        HotkeySettings setting,
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Update(setting, remove: true, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static HyprlandHotkeySyncResult Update(
        HotkeySettings setting,
        bool remove,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported)
        {
            return HyprlandHotkeySyncResult.NotApplicable;
        }

        if (setting.HotkeyInfo is null)
        {
            return HyprlandHotkeySyncResult.Failure("The hotkey does not have a key definition.");
        }

        string registrationId = setting.HotkeyInfo.RegistrationId;
        if (!Regex.IsMatch(registrationId ?? string.Empty, "^[A-Za-z0-9]{32}$"))
        {
            return HyprlandHotkeySyncResult.Failure("The hotkey has an invalid persistent identifier.");
        }

        string? entry = null;
        if (!remove)
        {
            if (setting.TaskSettings is null || setting.TaskSettings.Job == HotkeyType.None)
            {
                return HyprlandHotkeySyncResult.Failure("The hotkey does not have an executable action.");
            }

            if (!TryFormatKey(setting.HotkeyInfo, out string? key, out string? keyError))
            {
                return HyprlandHotkeySyncResult.Failure(keyError!);
            }

            HotkeyType job = setting.TaskSettings.Job;
            entry = string.Join(Environment.NewLine,
                $"-- SNAPX-HOTKEY: {registrationId} BEGIN",
                $"hl.unbind(\"{key}\")",
                $"o.bind(\"{key}\", \"SnapX {job}\", \"snapx-ui -{job}\")",
                $"-- SNAPX-HOTKEY: {registrationId} END",
                string.Empty);
        }

        string path = GetBindingsPath();
        string original;
        string? previousConfigErrors;
        try
        {
            original = File.ReadAllText(path);
            previousConfigErrors = GetConfigErrors();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return HyprlandHotkeySyncResult.Failure($"Could not read Hyprland bindings: {ex.Message}");
        }

        string updated = RemoveManagedEntry(original, registrationId);
        if (entry is not null)
        {
            updated = AddManagedEntry(updated, entry);
        }

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return HyprlandHotkeySyncResult.Success("The Hyprland binding was already up to date.");
        }

        try
        {
            WriteAtomically(path, updated);
            string? validationError = ReloadAndValidate();
            if (string.Equals(validationError, previousConfigErrors, StringComparison.Ordinal))
            {
                return HyprlandHotkeySyncResult.Success(
                    remove ? "The Hyprland binding was removed." : "The Hyprland binding is active.");
            }

            WriteAtomically(path, original);
            _ = ReloadAndValidate();
            return HyprlandHotkeySyncResult.Failure(
                $"Hyprland rejected the updated binding. The previous file was restored: {validationError}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return HyprlandHotkeySyncResult.Failure($"Could not update Hyprland bindings: {ex.Message}");
        }
    }

    private static string RemoveManagedEntry(string contents, string registrationId)
    {
        if (!TryFindManagedSection(contents, out int sectionStart, out int sectionEnd))
        {
            return contents;
        }

        const string lineBreak = "(?:\\r\\n|\\r|\\n)";
        const string lineStart = "(?<![^\\r\\n])";
        string pattern = $"(?s){lineStart}-- SNAPX-HOTKEY: {Regex.Escape(registrationId)} BEGIN{lineBreak}.*?{lineStart}-- SNAPX-HOTKEY: {Regex.Escape(registrationId)} END(?:{lineBreak})?";
        string section = contents[sectionStart..sectionEnd];
        string updatedSection = Regex.Replace(section, pattern, string.Empty);
        return contents[..sectionStart] + updatedSection + contents[sectionEnd..];
    }

    private static string AddManagedEntry(string contents, string entry)
    {
        string newLine = GetLineEnding(contents);
        entry = entry.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

        if (TryFindManagedSection(contents, out _, out int sectionEnd))
        {
            string separator = sectionEnd > 0 && !IsLineBreak(contents[sectionEnd - 1])
                ? newLine
                : string.Empty;
            return contents.Insert(sectionEnd, separator + entry);
        }

        string prefix = contents.Length == 0
            ? string.Empty
            : (IsLineBreak(contents[^1]) ? newLine : newLine + newLine);
        return contents + prefix + BeginMarker + newLine + entry + EndMarker + newLine;
    }

    private static void WriteAtomically(string path, string contents)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Hyprland bindings path has no parent directory.");
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.snapx-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, contents);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(path));
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool TryFindManagedSection(string contents, out int sectionStart, out int sectionEnd)
    {
        sectionStart = FindMarkerAtLineStart(contents, BeginMarker, 0);
        if (sectionStart < 0)
        {
            sectionEnd = -1;
            return false;
        }

        sectionEnd = FindMarkerAtLineStart(contents, EndMarker, sectionStart + BeginMarker.Length);
        return sectionEnd >= 0;
    }

    private static int FindMarkerAtLineStart(string contents, string marker, int startIndex)
    {
        for (int index = contents.IndexOf(marker, startIndex, StringComparison.Ordinal);
             index >= 0;
             index = contents.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            bool lineStart = index == 0 || IsLineBreak(contents[index - 1]);
            int afterMarker = index + marker.Length;
            bool lineEnd = afterMarker == contents.Length || IsLineBreak(contents[afterMarker]);
            if (lineStart && lineEnd)
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetLineEnding(string contents)
    {
        int carriageReturn = contents.IndexOf('\r');
        if (carriageReturn >= 0)
        {
            return carriageReturn + 1 < contents.Length && contents[carriageReturn + 1] == '\n'
                ? "\r\n"
                : "\r";
        }

        return "\n";
    }

    private static bool IsLineBreak(char value) => value is '\r' or '\n';

    private static string? ReloadAndValidate()
    {
        // Test-only escape hatches so automated tests can exercise the file
        // rewrite logic without invoking a real compositor. Neither variable
        // is documented for end users and both are ignored unless set.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPX_HYPR_FAKE_HYPRCTL_FAILURE")))
        {
            return "Simulated hyprctl failure for testing.";
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPX_HYPR_SKIP_RELOAD")))
        {
            return GetConfigErrors();
        }

        ProcessResult reload = RunHyprctl("reload");
        if (reload.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(reload.Error)
                ? "hyprctl reload failed."
                : reload.Error.Trim();
        }

        return GetConfigErrors();
    }

    private static string? GetConfigErrors()
    {
        // Lets the property suite cover how an unrelated pre-existing
        // compositor error is handled without querying the live compositor.
        string? simulatedErrors = Environment.GetEnvironmentVariable("SNAPX_HYPR_FAKE_CONFIG_ERRORS");
        if (simulatedErrors is not null)
        {
            return simulatedErrors;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPX_HYPR_SKIP_RELOAD")))
        {
            return null;
        }

        ProcessResult validation = RunHyprctl("configerrors");
        if (validation.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(validation.Error)
                ? "hyprctl configerrors failed."
                : validation.Error.Trim();
        }

        return string.IsNullOrWhiteSpace(validation.Output) ? null : validation.Output.Trim();
    }

    private static ProcessResult RunHyprctl(string argument)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "hyprctl",
                ArgumentList = { argument },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* The process already exited. */ }
            throw new InvalidOperationException("hyprctl did not respond within five seconds.");
        }
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static bool TryFormatKey(HotkeyInfo info, out string? key, out string? error)
    {
        string? keyName = info.KeyCode switch
        {
            >= Keys.A and <= Keys.Z => info.KeyCode.ToString(),
            >= Keys.D0 and <= Keys.D9 => ((int)info.KeyCode - (int)Keys.D0).ToString(),
            >= Keys.F1 and <= Keys.F24 => info.KeyCode.ToString(),
            Keys.PrintScreen => "PRINT",
            Keys.Return => "RETURN",
            Keys.Space => "SPACE",
            Keys.Tab => "TAB",
            Keys.Escape => "ESCAPE",
            Keys.Delete => "DELETE",
            Keys.Insert => "INSERT",
            Keys.Home => "HOME",
            Keys.End => "END",
            Keys.PageUp => "PAGE_UP",
            Keys.PageDown => "PAGE_DOWN",
            Keys.Left => "LEFT",
            Keys.Up => "UP",
            Keys.Right => "RIGHT",
            Keys.Down => "DOWN",
            _ => null
        };

        if (keyName is null)
        {
            key = null;
            error = $"{info.KeyCode} cannot be represented as a Hyprland binding.";
            return false;
        }

        var parts = new List<string>();
        if (info.Control) parts.Add("CTRL");
        if (info.Shift) parts.Add("SHIFT");
        if (info.Alt) parts.Add("ALT");
        if (info.Win) parts.Add("SUPER");
        parts.Add(keyName);
        key = string.Join(" + ", parts);
        error = null;
        return true;
    }

    private static bool IsHyprlandSession()
    {
        // Allows tests to exercise Hyprland-only behavior on hosts/sessions
        // that are not actually running Hyprland.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPX_HYPR_FORCE_SESSION")))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            return true;
        }

        string desktops = string.Join(' ',
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
        return desktops.Contains("Hyprland", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBindingsPath()
    {
        string? overriddenPath = Environment.GetEnvironmentVariable("SNAPX_HYPR_BINDINGS_PATH");
        return !string.IsNullOrWhiteSpace(overriddenPath)
            ? overriddenPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "hypr", "bindings.lua");
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);
}

public readonly record struct HyprlandHotkeySyncResult(bool IsApplicable, bool IsSuccess, string? Message)
{
    public static HyprlandHotkeySyncResult NotApplicable => new(false, true, null);
    public static HyprlandHotkeySyncResult Success(string message) => new(true, true, message);
    public static HyprlandHotkeySyncResult Failure(string message) => new(true, false, message);
}

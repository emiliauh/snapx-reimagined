// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Upload.Custom;

/// <summary>
/// Supplies optional user interaction for ShareX custom-uploader syntax without
/// coupling the uploader core to a GUI framework.
/// </summary>
public interface ICustomUploaderInteraction
{
    string? RequestInput(string title, string defaultText);
    void ShowOutput(string title, string text);
    string? Select(string title, IReadOnlyList<string> values);
}

/// <summary>
/// Deterministic non-interactive behavior suitable for CLI, service, and test use.
/// Input boxes keep their configured default, output boxes are informational no-ops,
/// and selection uses the first configured value.
/// </summary>
public sealed class HeadlessCustomUploaderInteraction : ICustomUploaderInteraction
{
    public static HeadlessCustomUploaderInteraction Instance { get; } = new();

    private HeadlessCustomUploaderInteraction()
    {
    }

    public string RequestInput(string title, string defaultText) => defaultText;

    public void ShowOutput(string title, string text)
    {
    }

    public string? Select(string title, IReadOnlyList<string> values) => values.Count > 0 ? values[0] : null;
}

public static class CustomUploaderInteraction
{
    private static ICustomUploaderInteraction current = HeadlessCustomUploaderInteraction.Instance;

    public static ICustomUploaderInteraction Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

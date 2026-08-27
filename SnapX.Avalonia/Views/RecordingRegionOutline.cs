// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SnapX.Core;
using SnapX.Core.Utils.Native;
using ImageRectangle = SixLabors.ImageSharp.Rectangle;

namespace SnapX.Avalonia.Views;

/// <summary>
/// Displays the live recording boundary without placing any visible pixels
/// inside the captured rectangle.
///
/// On Wayland/Hyprland the marker is drawn by a native wlr-layer-shell helper
/// (snapx-outline) that renders four 2px dashed red edges (dash 14, gap 6,
/// #FF2A2A) strictly OUTSIDE the region. Each edge has its own two-pixel layer
/// surface, so an alpha or compositor failure cannot obscure the selected
/// pixels. A layer-shell
/// OVERLAY surface is not a normal toplevel window:
/// it is absent from <c>hyprctl clients</c>, has no decorations/taskbar/focus,
/// is click-through, and is excluded from grim/wf-recorder capture so the
/// marker is never recorded.
///
/// On non-Wayland (or if the helper is unavailable) it falls back to four thin
/// Avalonia windows placed outside the region.
/// </summary>
public static class RecordingRegionOutline
{
    private const int Thickness = 1;
    private const string HelperName = "snapx-outline";

    private static readonly List<OutlineSegmentWindow> Segments = [];
    private static readonly List<Process> HelperProcesses = [];
    private static readonly object HelperProcessesLock = new();

    public static void Show(ImageRectangle captureRectangle)
    {
        if (captureRectangle.Width <= 0 || captureRectangle.Height <= 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            HideCore();

            if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland() && TryStartWaylandHelper(captureRectangle))
            {
                return;
            }

            // Keep every red pixel outside the exact region given to the
            // recorder. The corner overhang makes the outline continuous.
            Add(captureRectangle.X - Thickness, captureRectangle.Y - Thickness,
                captureRectangle.Width + Thickness * 2, Thickness);
            Add(captureRectangle.X - Thickness, captureRectangle.Y + captureRectangle.Height,
                captureRectangle.Width + Thickness * 2, Thickness);
            Add(captureRectangle.X - Thickness, captureRectangle.Y,
                Thickness, captureRectangle.Height);
            Add(captureRectangle.X + captureRectangle.Width, captureRectangle.Y,
                Thickness, captureRectangle.Height);
        });
    }

    public static void Hide() => Dispatcher.UIThread.Post(HideCore);

    /// <summary>
    /// Resolves the output containing a capture rectangle. The interactive
    /// recording controller uses the same calculation as the outline so its
    /// layer surface stays on the monitor being recorded rather than merely
    /// whichever output happens to be enumerated first by Wayland.
    /// </summary>
    internal static bool TryGetCaptureOutputName(ImageRectangle region, out string? outputName)
    {
        return TryResolveOutputLocalRegion(region, out outputName, out _, out _, out _, out _, out _,
            out _, out _);
    }

    /// <summary>
    /// Resolves the output-local geometry shared by the native outline and
    /// controller surfaces. Layer-shell margins are relative to an output, so
    /// passing compositor-global capture coordinates would place the controller
    /// on the wrong edge on secondary monitors.
    /// </summary>
    /// <remarks>
    /// <paramref name="outputLogicalWidth"/>/<paramref name="outputLogicalHeight"/> are the
    /// output's TRUE fractional-logical size (pixel mode divided by the fractional scale),
    /// which is the coordinate space the compositor interprets layer-shell margins in.
    /// The native helper cannot derive it itself: wl_output.scale is an integer, so a
    /// 2560x1440 output at scale 1.6 is advertised as scale 2 and would be mistaken for a
    /// 1280x720 output instead of the real 1600x900 space.
    /// <paramref name="workAreaTop"/>/<paramref name="workAreaBottom"/> bound the output's
    /// usable band in that same logical space, after the compositor's reserved top and
    /// bottom insets. The controller clamps itself to that band so a card for a region near
    /// the bottom of the output does not slide underneath a reserved dock.
    /// </remarks>
    internal static bool TryGetCaptureOutputRegion(ImageRectangle region, out string? outputName,
        out int localX, out int localY, out int outputLogicalWidth, out int outputLogicalHeight,
        out int workAreaTop, out int workAreaBottom)
    {
        return TryResolveOutputLocalRegion(region, out outputName, out localX, out localY, out _,
            out outputLogicalWidth, out outputLogicalHeight, out workAreaTop, out workAreaBottom);
    }

    private static bool TryStartWaylandHelper(ImageRectangle region)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !LinuxAPI.IsWayland())
            {
                return false;
            }

            string? helper = ResolveHelperPath();
            if (helper is null)
            {
                return false;
            }

            // Convert the compositor-global capture rectangle to the target
            // output's local logical coordinates (origin at the output's
            // top-left after the top panel/bar reserved area).
            if (!TryResolveOutputLocalRegion(region, out string? outputName,
                    out int localX, out int localY, out bool coversWholeOutput, out _, out _,
                    out _, out _))
            {
                // Could not find the owning output; fall back to the Avalonia
                // windows rather than drawing a misplaced overlay.
                return false;
            }

            if (coversWholeOutput)
            {
                // An outline is deliberately outside the recorded rectangle.
                // A region that already fills its output therefore has no
                // drawable border on that output. More importantly, a
                // full-output transparent layer is the exact failure mode
                // that can look like a black screen on a compositor or driver
                // transparency failure, so do not create one at all.
                Debug.WriteLine("SnapX: skipping the native outline for a full-output recording.");
                return true;
            }

            string[] edges = ["top", "bottom", "left", "right"];
            foreach (string edge in edges)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = helper,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(localX.ToString());
                startInfo.ArgumentList.Add(localY.ToString());
                startInfo.ArgumentList.Add(region.Width.ToString());
                startInfo.ArgumentList.Add(region.Height.ToString());
                startInfo.ArgumentList.Add("--edge");
                startInfo.ArgumentList.Add(edge);
                if (!string.IsNullOrWhiteSpace(outputName))
                {
                    startInfo.ArgumentList.Add("--output");
                    startInfo.ArgumentList.Add(outputName);
                }

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    HideHelperProcesses();
                    return false;
                }

                lock (HelperProcessesLock)
                {
                    HelperProcesses.Add(process);
                }
                // If an edge helper exits unexpectedly (crash, compositor
                // teardown, stale helper), do not let the outline claim a
                // marker that is no longer there. Log it and remove that
                // process from the list so a subsequent Show() starts fresh
                // instead of accumulating dead entries.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.WriteLine($"Recording outline helper watchdog wait failed: {ex.Message}");
                        return;
                    }

                    bool removed;
                    lock (HelperProcessesLock)
                    {
                        removed = HelperProcesses.Remove(process);
                    }
                    if (!removed)
                    {
                        return;
                    }
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.WriteLine($"Recording outline helper watchdog dispose failed: {ex.Message}");
                    }
                    DebugHelper.WriteLine("Recording outline native helper exited unexpectedly.");
                });
            }

            lock (HelperProcessesLock)
            {
                return HelperProcesses.Count > 0;
            }
        }
        catch
        {
            HideHelperProcesses();
            return false;
        }
    }

    private static bool TryResolveOutputLocalRegion(ImageRectangle region,
        out string? outputName, out int localX, out int localY, out bool coversWholeOutput,
        out int outputLogicalWidth, out int outputLogicalHeight,
        out int workAreaTop, out int workAreaBottom)
    {
        static bool IsQuarterTurn(int transform) => transform is 1 or 3 or 5 or 7;

        outputName = null;
        localX = region.X;
        localY = region.Y;
        coversWholeOutput = false;
        outputLogicalWidth = 0;
        outputLogicalHeight = 0;
        workAreaTop = 0;
        workAreaBottom = 0;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    ArgumentList = { "-j", "monitors" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return false;
            }

            int centerX = region.X + region.Width / 2;
            int centerY = region.Y + region.Height / 2;

            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement monitor in document.RootElement.EnumerateArray())
            {
                if (!monitor.TryGetProperty("name", out JsonElement nameEl) ||
                    !monitor.TryGetProperty("x", out JsonElement xEl) ||
                    !monitor.TryGetProperty("y", out JsonElement yEl) ||
                    !monitor.TryGetProperty("width", out JsonElement wEl) ||
                    !monitor.TryGetProperty("height", out JsonElement hEl) ||
                    !monitor.TryGetProperty("scale", out JsonElement scaleEl))
                {
                    continue;
                }

                double scale = scaleEl.GetDouble();
                if (scale <= 0)
                {
                    scale = 1;
                }

                int transform = monitor.TryGetProperty("transform", out JsonElement tEl)
                    ? tEl.GetInt32()
                    : 0;
                bool rotated = IsQuarterTurn(transform);
                int logicalW = (int)Math.Round((rotated ? hEl.GetDouble() : wEl.GetDouble()) / scale);
                int logicalH = (int)Math.Round((rotated ? wEl.GetDouble() : hEl.GetDouble()) / scale);
                int monX = (int)Math.Round(xEl.GetDouble());
                int monY = (int)Math.Round(yEl.GetDouble());

                if (centerX >= monX && centerX < monX + logicalW &&
                    centerY >= monY && centerY < monY + logicalH)
                {
                    outputName = nameEl.GetString();
                    localX = region.X - monX;
                    localY = region.Y - monY;
                    outputLogicalWidth = logicalW;
                    outputLogicalHeight = logicalH;
                    coversWholeOutput = region.X <= monX && region.Y <= monY &&
                        (long)region.X + region.Width >= (long)monX + logicalW &&
                        (long)region.Y + region.Height >= (long)monY + logicalH;

                    // Subtract the output's reserved top inset so the outline
                    // aligns with the compositor work area the layer surface
                    // occupies. Hyprland reports reserved as [left, top, right, bottom].
                    // The same insets bound the work area the controller must
                    // stay inside, so keep both the top and bottom edges.
                    int reservedTop = 0;
                    int reservedBottom = 0;
                    if (monitor.TryGetProperty("reserved", out JsonElement reservedEl) &&
                        reservedEl.ValueKind == JsonValueKind.Array &&
                        reservedEl.GetArrayLength() >= 2 &&
                        reservedEl[1].ValueKind == JsonValueKind.Number)
                    {
                        reservedTop = reservedEl[1].GetInt32();
                        localY -= reservedTop;
                        if (reservedEl.GetArrayLength() >= 4 &&
                            reservedEl[3].ValueKind == JsonValueKind.Number)
                        {
                            reservedBottom = reservedEl[3].GetInt32();
                        }
                    }

                    // Usable logical band: below the top inset, above the
                    // bottom one. Invalid insets (negative, or larger than
                    // the output) are dropped so the helper falls back to the
                    // full output height rather than clamping to a phantom band.
                    if (reservedTop >= 0 && reservedBottom >= 0 &&
                        reservedTop + reservedBottom < logicalH)
                    {
                        workAreaTop = reservedTop;
                        workAreaBottom = logicalH - reservedBottom;
                    }

                    return true;
                }
            }
        }
        catch
        {
            // Fall through to the caller's fallback.
        }

        return false;
    }

    private static string? ResolveHelperPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, HelperName),
            Path.Combine(baseDir, "native", HelperName),
            Path.Combine(baseDir, "lib", "snapx", HelperName),
            HelperName
        ];

        foreach (string candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string full = Path.Combine(dir, HelperName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static void Add(int x, int y, int width, int height)
    {
        var segment = new OutlineSegmentWindow(new PixelPoint(x, y), width, height);
        Segments.Add(segment);
        segment.Show();
    }

    private static void HideCore()
    {
        lock (HelperProcessesLock)
        {
            if (HelperProcesses.Count > 0)
            {
                HideHelperProcesses();
                return;
            }
        }

        foreach (OutlineSegmentWindow segment in Segments)
        {
            segment.Close();
        }
        Segments.Clear();
    }

    private static void HideHelperProcesses()
    {
        Process[] helpers;
        lock (HelperProcessesLock)
        {
            helpers = HelperProcesses.ToArray();
            HelperProcesses.Clear();
        }
        foreach (Process helper in helpers)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.StandardInput.WriteLine("quit");
                    helper.StandardInput.Flush();
                    if (!helper.WaitForExit(1000))
                    {
                        helper.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                try { helper.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
            }
            finally
            {
                helper.Dispose();
            }
        }

    }

    private sealed class OutlineSegmentWindow : Window
    {
        public OutlineSegmentWindow(PixelPoint position, int width, int height)
        {
            SystemDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            Title = "\u200B";
            Topmost = true;
            CanResize = false;
            Focusable = false;
            Width = width;
            Height = height;
            Position = position;
            Background = Brushes.Transparent;
            TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Content = new DashSurface(width, height, width > height);
            IsHitTestVisible = false;
            Opened += (_, _) =>
            {
                if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
                {
                    Close();
                    Segments.Remove(this);
                }
            };
        }
    }

    private sealed class DashSurface : Control
    {
        private const int DashLength = 8;
        private const int DashGap = 8;
        private readonly bool horizontal;

        public DashSurface(int width, int height, bool horizontal)
        {
            this.horizontal = horizontal;
            Width = width;
            Height = height;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            int length = horizontal ? (int)Bounds.Width : (int)Bounds.Height;
            int thickness = horizontal ? (int)Bounds.Height : (int)Bounds.Width;
            for (int offset = 0; offset < length; offset += DashLength + DashGap)
            {
                int dash = Math.Min(DashLength, length - offset);
                var rect = horizontal
                    ? new Rect(offset, 0, dash, thickness)
                    : new Rect(0, offset, thickness, dash);
                context.FillRectangle(Brushes.Red, rect);
            }
        }
    }
}

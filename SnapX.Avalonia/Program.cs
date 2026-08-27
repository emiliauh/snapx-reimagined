// See https://aka.ms/new-console-template for more information
#pragma warning disable CA1416 // I am aware
using System.Reflection;
using Avalonia;
using SnapX.Avalonia;
using SnapX.Avalonia.Utils;
#if BROWSER
using Avalonia.Browser;
#else
using Avalonia.Media;
#endif

internal static class Program
{
    /// <summary>
    /// True when this process selected Avalonia's native Wayland backend
    /// (<c>UseWayland</c>) instead of X11/Xwayland.
    ///
    /// The two backends resolve <c>ITrayIconImpl</c> differently:
    /// Avalonia.X11 ships both XEmbedTrayIconImpl and DBusTrayIconImpl,
    /// while Avalonia.Wayland only ever creates a DBusTrayIconImpl. Callers
    /// that need to know whether the XEmbed tray path is reachable must test
    /// the backend, not <c>XDG_SESSION_TYPE</c>: a Wayland session can still
    /// host this process through Xwayland.
    /// </summary>
    internal static bool IsNativeWaylandBackend { get; private set; }
    internal static SingleInstanceManager? ForwardedPrimaryInstance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length != 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            var informationalVersion =
                Assembly
                    .GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "Unknown";

            Console.WriteLine(informationalVersion);
            return;
        }

        if (SingleInstanceManager.TryForward(args, out var primaryInstance))
        {
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(0);
            return;
        }

        ForwardedPrimaryInstance = primaryInstance;

        BuildAvaloniaApp()
#if !BROWSER
        .StartWithClassicDesktopLifetime(args);
#else
        .StartBrowserAppAsync("out");
#endif
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
#if BROWSER
        return builder;
#else
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            builder = builder.With(
                new FontManagerOptions
                {
                    FontFallbacks = new List<FontFallback>
                    {
                        new() { FontFamily = "Noto Sans" },
                        new() { FontFamily = "Roboto" },
                        new() { FontFamily = "Adwaita Sans" },
                        new() { FontFamily = "Open Sans" },
                        new() { FontFamily = "Segoe UI" },
                        new() { FontFamily = "Inter" }, // kept for compatibility
                        new() { FontFamily = "Helvetica Neue" },
                    },
                }
            );
        }

        builder = builder.LogToTrace();

        var x11Options = new X11PlatformOptions
        {
#if FREEBSD
            RenderingMode = [
                //X11RenderingMode.Vulkan, // For some reason, I could not get Vulkan Rendering mode working on my FreeBSD VM
                X11RenderingMode.Egl,
                X11RenderingMode.Glx,
                X11RenderingMode.Software],
            UseGLibMainLoop = true, // Without this option, the application does not start correctly on this platform.
#else
            // SnapX is currently hosted through XWayland on this Linux path.
            // NVIDIA's Vulkan presenter has produced native crashes during
            // window resize and overlay updates. Prefer EGL/GLX; Vulkan stays
            // available for explicit diagnostic opt-in.
            RenderingMode = Environment.GetEnvironmentVariable("SNAPX_USE_VULKAN") == "1"
                ? [X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software]
                : [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
#endif
            UseRetainedFramebuffer = true,
            OverlayPopups = true,
        };

        if (
            OperatingSystem.IsFreeBSD()
            || Environment.GetEnvironmentVariable("SNAPX_PRETEND_FREEBSD") is not null
        )
        {
#if FREEBSD
            builder = builder.UseSkia().UseX11().With(x11Options);
#endif
        }
        else
        {
            // On Linux/Wayland, prefer the native Wayland backend so drag-and-drop
            // and clipboard use the wl_data_device protocol directly against the
            // compositor (Hyprland). XWayland (UsePlatformDetect -> UseX11) only
            // speaks X11 XDND, which cannot hand a file URI to a native Wayland
            // drop target (file manager, browser upload control, etc.). Fall back
            // to X11 when no Wayland session is present so X11 desktops keep working.
            bool isWaylandSession =
                string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

            if (isWaylandSession)
            {
                // The crash on this session is a native SIGSEGV inside
                // libnvidia-eglcore on the "AvaloniaWayland" thread, preceded by
                // thousands of swallowed "eglMakeCurrent failed with error
                // EGL_SUCCESS" render-loop failures. Avalonia.Wayland builds its
                // GPU render surface from WaylandEglWsiPlatformGraphics.TryCreate,
                // and WSurface.RenderSurfaces falls back to the software
                // WaylandFramebuffer whenever that returns null. TryCreate swallows
                // its construction exception, and EglDisplayUtils
                // .InitializeAndGetConfig throws "No suitable EGL config was found"
                // for an empty version list, so an empty GlProfiles list is the
                // supported way to keep this process off the NVIDIA EGL driver
                // entirely. Set SNAPX_WAYLAND_GPU=1 to opt back into EGL.
                bool waylandGpu = Environment.GetEnvironmentVariable("SNAPX_WAYLAND_GPU") == "1";

                IsNativeWaylandBackend = true;
                builder = builder
                    .UseHarfBuzz()
                    .UseSkia()
                    .UseWayland()
                    .With(new WaylandPlatformOptions
                    {
                        UseGLibMainLoop = true,
                        GlProfiles = waylandGpu
                            ? new WaylandPlatformOptions().GlProfiles
                            : new List<Avalonia.OpenGL.GlVersion>(),
                    })
                    .With(x11Options)
                    .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
                    .With(new Win32PlatformOptions { OverlayPopups = true });
            }
            else
            {
                builder = builder
                    .UseHarfBuzz()
                    .UsePlatformDetect()
                    .With(x11Options)
                    .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
                    .With(new Win32PlatformOptions { OverlayPopups = true });
            }
        }

        return builder;
#endif
    }
}

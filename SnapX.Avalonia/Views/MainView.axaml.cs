using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using FluentAvalonia.UI.Controls;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.Views.Controls;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Miscellaneous;
using Image = SixLabors.ImageSharp.Image;

namespace SnapX.Avalonia.Views;

public partial class MainView : UserControl
{
    private string? selectedAction;
    private TimeSpan? delay;
    private bool _isVideoMode;

    public MainView()
    {
        InitializeComponent();
        var _flyout = CaptureSplitButton;
        if (_flyout != null)
        {
            foreach (var item in _flyout.MenuItems)
            {
                if (item is FANavigationViewItem menuItem)
                {
                    if (menuItem.Tag != null)
                        return;
                    if (menuItem.Name == "DelayMenuItem")
                        continue;
                    menuItem.PointerPressed += (Sender, Args) =>
                        SelectCaptureActionCommand.Execute(menuItem.Content as string);
                }
            }
        }
    }

    [RelayCommand]
    private async Task ExecuteSelectedCaptureAction(string? theAction = null)
    {
        var action = selectedAction ?? theAction;
        DebugHelper.WriteLine($"Executing: {action}");
        Image? img = null;
        var actionMap = new Dictionary<string, Func<Task>>
        {
            [Lang.UI_Capture_Fullscreen] = async () =>
            {
                if (_isVideoMode)
                {
                    DebugHelper.WriteLine("ExecuteSelectedCaptureAction: starting screen recording (Fullscreen).");
                    TaskHelpers.StartScreenRecording(ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.Fullscreen);
                    return;
                }
                await TaskHelpers.ExecuteJob(TaskSettings.GetDefaultTaskSettings(), HotkeyType.PrintScreen);
            },
            [Lang.UI_Dropdown_Region] = async () =>
            {
                DebugHelper.WriteLine($"ExecuteSelectedCaptureAction: Region clicked, _isVideoMode={_isVideoMode}.");
                if (_isVideoMode)
                {
                    DebugHelper.WriteLine("ExecuteSelectedCaptureAction: starting screen recording (Region).");
                    TaskHelpers.StartScreenRecording(ScreenRecordOutput.FFmpeg, ScreenRecordStartMethod.Region);
                    return;
                }
                await TaskHelpers.ExecuteJob(TaskSettings.GetDefaultTaskSettings(), HotkeyType.RectangleRegion);
            },
            [Lang.UI_Dropdown_ScrollingCapture] = async () =>
            {
                DebugHelper.WriteLine("ExecuteSelectedCaptureAction: ScrollingCapture clicked.");
                TaskHelpers.OpenScrollingCapture(TaskSettings.GetDefaultTaskSettings());
                await Task.CompletedTask;
            },
            [Lang.UI_Dropdown_Annotate] = async () =>
            {
                DebugHelper.WriteLine("ExecuteSelectedCaptureAction: Annotate clicked.");
                TaskHelpers.OpenImageEditor(TaskSettings.GetDefaultTaskSettings());
                await Task.CompletedTask;
            },
            [Lang.UI_Dropdown_RegionLight] = async () =>
            {
                await TaskHelpers.ExecuteJob(TaskSettings.GetDefaultTaskSettings(), HotkeyType.RectangleLight);
            },
            [Lang.UI_Dropdown_RegionTransparent] = async () =>
            {
                await TaskHelpers.ExecuteJob(TaskSettings.GetDefaultTaskSettings(), HotkeyType.RectangleTransparent);
            },
            [Lang.UI_Dropdown_Window] = async () =>
            {
                if (_isVideoMode)
                {
                    await SelectWindowForRecordingAsync();
                    return;
                }
                // The hotkey/CLI HotkeyType.ActiveWindow path still grabs
                // whichever window currently has focus, which is what a
                // scripted invocation expects. The sidebar button is
                // interactive, so let the user hover to pick which window
                // instead of guessing which one was "active".
                new SnapX.Core.Capture.CaptureWindowPicker().Capture(TaskSettings.GetDefaultTaskSettings());
                return;
            },
            [Lang.UI_Dropdown_Monitor] = () =>
            {
                if (_isVideoMode)
                {
                    return SelectMonitorForRecordingAsync();
                }
                new SnapX.Core.Capture.CaptureMonitorPicker().Capture(TaskSettings.GetDefaultTaskSettings());
                return Task.CompletedTask;
            },
            [Lang.UI_Dropdown_ScreenRecording] = async () =>
            {
                // var rect = new RegionSelectorWindow(new RegionSelectorViewModel()).Show();
                // var
                TaskHelpers.StartScreenRecording(
                    ScreenRecordOutput.FFmpeg,
                    ScreenRecordStartMethod.Region
                );
            },
        };
        if (action != null && actionMap.TryGetValue(action, out var func))
        {
            if (delay != null && delay.HasValue)
                await Task.Delay((int)delay.Value.TotalMilliseconds);
            await func();
        }
        else
        {
            DebugHelper.WriteLine("No matching action found.");
        }

        if (img != null)
            UploadManager.RunImageTask(img, TaskSettings.GetDefaultTaskSettings());
    }

    private void CaptureModeToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        _isVideoMode = CaptureModeToggle.IsChecked == true;
        CaptureModeIcon.Symbol = _isVideoMode ? FluentIcons.Common.Symbol.Video : FluentIcons.Common.Symbol.Camera;
        CaptureModeLabel.Text = _isVideoMode ? "Video" : "Photo";
        DebugHelper.WriteLine($"CaptureModeToggle_OnClick: mode is now {(_isVideoMode ? "Video" : "Photo")}.");
    }

    /// <summary>
    /// Selects a recording window interactively. The compositor-native picker
    /// highlights the window under the pointer before a click commits it, so
    /// Video > Window never starts recording whichever window merely happens
    /// to be focused at the time the menu item is pressed.
    /// </summary>
    private static async Task SelectWindowForRecordingAsync()
    {
        TaskSettings taskSettings = TaskSettings.GetDefaultTaskSettings();
        RegionCaptureOptions options = RegionCaptureTasks.GetRegionCaptureOptions(
            taskSettings.CaptureSettings.SurfaceOptions);
        options.WindowPickerMode = true;

        RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(
            options,
            RegionCaptureType.Default,
            captureImage: false);
        if (selection is null || selection.Rectangle.IsEmpty)
        {
            DebugHelper.WriteLine("Video window selection was cancelled before recording started.");
            return;
        }

        taskSettings.CaptureSettings.CaptureCustomRegion = selection.Rectangle;
        TaskHelpers.StartScreenRecording(
            ScreenRecordOutput.FFmpeg,
            ScreenRecordStartMethod.CustomRegion,
            taskSettings);
    }

    /// <summary>
    /// Selects a recording monitor interactively. The compositor-native
    /// monitor picker highlights the output under the pointer before a click
    /// commits it, so Video > Monitor never starts recording whichever
    /// monitor merely happens to be focused at the time the menu item is
    /// pressed. Mirrors <see cref="SelectWindowForRecordingAsync"/>.
    /// </summary>
    private static async Task SelectMonitorForRecordingAsync()
    {
        TaskSettings taskSettings = TaskSettings.GetDefaultTaskSettings();
        RegionCaptureOptions options = RegionCaptureTasks.GetRegionCaptureOptions(
            taskSettings.CaptureSettings.SurfaceOptions);
        options.MonitorPickerMode = true;

        RegionCaptureSelection? selection = await RegionCaptureTasks.SelectRegionAsync(
            options,
            RegionCaptureType.Default,
            captureImage: false);
        if (selection is null || selection.Rectangle.IsEmpty)
        {
            DebugHelper.WriteLine("Video monitor selection was cancelled before recording started.");
            return;
        }

        taskSettings.CaptureSettings.CaptureCustomRegion = selection.Rectangle;
        TaskHelpers.StartScreenRecording(
            ScreenRecordOutput.FFmpeg,
            ScreenRecordStartMethod.CustomRegion,
            taskSettings);
    }

    private void AfterCaptureUploadItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is FAToggleMenuFlyoutItem toggle && SnapX.Avalonia.Utils.TaskFlagMenuHelper.Toggle(toggle))
        {
            SnapXL.Settings?.SaveAsync();
        }
    }

    private void AfterCaptureUploadFlyout_OnOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout flyout) return;
        foreach (var item in flyout.Items)
        {
            if (item is FAToggleMenuFlyoutItem toggle) SnapX.Avalonia.Utils.TaskFlagMenuHelper.SyncCheckState(toggle);
        }
    }

    [RelayCommand]
    private async Task ExecuteSelectedTool(string action)
    {
        var actionMap = new Dictionary<string, Func<Task>>
        {
            ["QR Code"] = async () =>
            {
                var qrWindow = new QRCodeView();
                qrWindow.Show();
            },
            ["OCR"] = async () =>
            {
                var ocrWindow = new OCR();
                ocrWindow.Show();
            }
        };

        if (action != null && actionMap.TryGetValue(action, out var func))
        {
            await func();
        }
        else
        {
            DebugHelper.WriteLine("No matching tool found.");
        }
    }

    private void DelayOption_Checked(object? sender, RoutedEventArgs e)
    {
        DebugHelper.WriteLine("DelayOption_Checked");
        if (sender is not FANavigationViewItem item)
            return;
        if (item.Tag is null)
            return;

        delay = TimeSpan.FromSeconds(long.Parse(item.Tag as string));
        Core.SnapXL.Settings.DefaultTaskSettings.CaptureSettings.ScreenshotDelay = (decimal)
            delay.Value.TotalSeconds;
        var DelayMenuItem = this.FindControl<FANavigationViewItem>("DelayMenuItem");
        if (DelayMenuItem == null || DelayMenuItem.MenuItems == null)
            return;

        long targetSeconds = (long)delay.Value.TotalSeconds;

        foreach (var menuItem in DelayMenuItem.MenuItems.Cast<FANavigationViewItem>())
        {
            if (menuItem.Tag is string tag && long.TryParse(tag, out long tagValue))
            {
                if (tagValue == targetSeconds)
                {
                    // menuItem.IsSelected = true;
                    var content = menuItem.Content as string;
                    if (!content.StartsWith("✓ "))
                        menuItem.Content = "✓ " + content;
                }
                else
                {
                    if (menuItem.Content is string content && content.StartsWith("✓ "))
                    {
                        menuItem.Content = content.Substring(2);
                    }
                }
            }
        }
        Core.SnapXL.Settings.SaveAsync();
    }

    [RelayCommand]
    private void SelectCaptureAction(string action)
    {
        DebugHelper.WriteLine($"Selecting: {action}");
        selectedAction = action;

        ExecuteSelectedCaptureActionCommand.ExecuteAsync(action);
    }

    private void AboutItem_Pressed(object? Sender, PointerPressedEventArgs E)
    {
        App.CreateAboutWindowStatic();
    }

    private void SettingsItem_Pressed(object? Sender, PointerPressedEventArgs E)
    {
        if (DataContext is MainViewModel mainViewModel)
            mainViewModel.CurrentPage = Ioc.Default.GetRequiredService<InAppSettingsHostVM>();
    }

    private void FindURLOnDescendant(ILogical control)
    {
        foreach (var child in control.GetLogicalChildren())
        {
            var toolTip = child.FindLogicalDescendantOfType<ToolTip>(true);
            if (toolTip is null)
            {
                FindURLOnDescendant(child);
            }

            var url = toolTip?.Content as string ?? string.Empty;
            if (!string.IsNullOrEmpty(url))
                URLHelpers.OpenURL(url);
        }
    }
    private void FindPathOnDescendant(ILogical control)
    {
        foreach (var child in control.GetLogicalChildren())
        {
            var toolTip = child.FindLogicalDescendantOfType<ToolTip>(true);
            if (toolTip is null)
            {
                FindPathOnDescendant(child);
            }

            var path = toolTip?.Content as string ?? string.Empty;
            if (!string.IsNullOrEmpty(path))
                FileHelpers.OpenFolder(path);
        }
    }
    private void DynamicURL_OnPointerPressed(object? Sender, PointerPressedEventArgs E)
    {
        DebugHelper.WriteLine($"{nameof(DynamicURL_OnPointerPressed)}: {Sender} {E.Source}");
        if (Sender is Control control)
        {
            // The ToolTip class has a storage of loaded tooltips, however, when a user clicks without hovering for a second the button didn't work.
            // So I added the second if-clause.
            if (ToolTip.GetTip(control) is string url)
            {
                URLHelpers.OpenURL(url);
                return;
            }

            FindURLOnDescendant(control);
        }
        else
        {
            DebugHelper.WriteLine(
                $"{nameof(DynamicURL_OnPointerPressed)} called with {Sender} which is not a Control!!"
            );
        }
    }
    private void DynamicFolder_OnPointerPressed(object? Sender, PointerPressedEventArgs E)
    {
        DebugHelper.WriteLine($"{nameof(DynamicFolder_OnPointerPressed)}: {Sender} {E.Source}");
        if (Sender is Control control)
        {
            // The ToolTip class has a storage of loaded tooltips, however, when a user clicks without hovering for a second the button didn't work.
            // So I added the second if-clause.
            if (ToolTip.GetTip(control) is string path)
            {
                FileHelpers.OpenFolder(path);
                return;
            }

            FindPathOnDescendant(control);
        }
        else
        {
            DebugHelper.WriteLine(
                $"{nameof(DynamicFolder_OnPointerPressed)} called with {Sender} which is not a Control!!"
            );
        }
    }
    private void OpenDebugLog(object? Sender, PointerPressedEventArgs E)
    {
        var window = new LogViewer();
        window.Show(App.MyMainWindow!);
    }

    private void MainView_OnInit(object? Sender, EventArgs E)
    {
        delay = TimeSpan.FromSeconds(
            (long)Core.SnapXL.Settings.DefaultTaskSettings.CaptureSettings.ScreenshotDelay
        );

        var MainNavView = this.FindControl<FANavigationView>("MainNavView");
        if (MainNavView != null)
        {
            MainNavView.Loaded -= MainNavView_Loaded_SetSelection;
            MainNavView.Loaded += MainNavView_Loaded_SetSelection;
        }
    }

    private void MainNavView_Loaded_SetSelection(object? sender, RoutedEventArgs e)
    {
        if (sender is not FANavigationView MainNavView)
            return;

        MainNavView.Loaded -= MainNavView_Loaded_SetSelection;

        var DelayMenuItem = MainNavView.FindControl<FANavigationViewItem>("DelayMenuItem");
        if (DelayMenuItem == null || DelayMenuItem.MenuItems == null)
            return;

        long targetSeconds = (long)delay.Value.TotalSeconds;

        foreach (var item in DelayMenuItem.MenuItems.Cast<FANavigationViewItem>())
        {
            if (item.Tag is string tag && long.TryParse(tag, out long tagValue))
            {
                if (tagValue == targetSeconds)
                {
                    item.IsSelected = true;
                    var content = item.Content as string;
                    item.Content = "✓ " + content;
                    return;
                }
            }
        }
    }

    private void DonateButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        var donationMenu = new Donation();
        var dialog = new FAContentDialog
        {
            Title = Lang.KeepSnapXOpenAndFree,
            Content = donationMenu,
            IsPrimaryButtonEnabled = true,
            PrimaryButtonText = Lang.CountMeIn,
            IsSecondaryButtonEnabled = true,
            SecondaryButtonText = Lang.MaybeLater,
            DefaultButton = FAContentDialogButton.Primary,
            PrimaryButtonCommand = donationMenu.PrimaryClickCommand,
            FullSizeDesired = true,
        };
        if (App.MyMainWindow != null)
            dialog.ShowAsync(App.MyMainWindow);
        else
            dialog.ShowAsync();
    }

    private async void DynamicDebugPressed(object? Sender, PointerPressedEventArgs E)
    {
        try
        {
            if (Sender is not FANavigationViewItem navigationViewItem)
                return;
            var target = navigationViewItem.Content as string;
            if (string.IsNullOrEmpty(target))
                return;
            DebugHelper.WriteLine($"{nameof(DynamicDebugPressed)}: {target}");
            var actionMap = new Dictionary<string, Func<Task>>
            {
                [Lang.UI_Debug_TestImageUpload] = async () =>
                {
                    UploadManager.UploadImage(
                        await WebHelpers.DownloadImageAsync(
                            $"{Links.GitHub}/blob/main/.github/Linux.png?raw=true"
                        )
                    );
                },
                [Lang.UI_Debug_TestTextUpload] = async () =>
                {
                    UploadManager.UploadText(
                        "This is a test text upload from SnapX, a fork of ShareX"
                    );
                },
                [Lang.UI_Debug_TestFileUpload] = async () =>
                {
                    UploadManager.DownloadAndUploadFile(
                        $"{Links.GitHub}/raw/main/.github/Progress.md"
                    );
                },
                [Lang.UI_Debug_TestURLShortener] = async () =>
                {
                    UploadManager.ShortenURL(Links.Website);
                },
                [Lang.UI_Debug_TestURLSharing] = async () =>
                {
                    UploadManager.ShareURL(Links.Website);
                },
            };
            if (actionMap.TryGetValue(target, out var func))
            {
                await func();
            }
            else
            {
                DebugHelper.WriteLine("No matching action found.");
            }
        }
        catch (Exception e)
        {
            e.ShowError();
        }
    }

    private void ToolClicked(object? Sender, PointerPressedEventArgs E)
    {
        ExecuteSelectedToolCommand.Execute((Sender as FANavigationViewItem).Content);
    }
}

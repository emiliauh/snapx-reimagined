using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FluentAvalonia.UI.Controls;
using SnapX.Avalonia.Views.Settings.Views;
using SnapX.Core;
using SnapX.Core.Utils.Native;

namespace SnapX.Avalonia.Utils;

public static class MacOSPermissionSetup
{
    private static readonly object DialogLock = new();
    private static Task<bool>? _activeDialog;

    public static async Task OnFirstLaunchAsync(Window owner)
    {
        if (!OperatingSystem.IsMacOS()) return;

        // TCC grants are tied to the signed app identity, not our application
        // settings. Re-check the effective grants on every launch so an update,
        // rebuilt local bundle, or revoked permission cannot be hidden by a
        // previously persisted "setup complete" value.
        bool permissionsComplete = MacOSPermissions.HasScreenCaptureAccess();
        if (!SnapXL.Settings.MacOSPermissionSetupDismissed || !permissionsComplete)
        {
            if (!await ShowAsync(owner, firstLaunch: true))
            {
                App.RequestShutdown();
                return;
            }
        }
        await MacOSStartupPrompt.OfferOnceAsync(owner);
    }

    public static Task<bool> ShowAsync(Window? owner, bool firstLaunch = false)
    {
        lock (DialogLock)
        {
            if (_activeDialog is not null) return _activeDialog;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> dialogTask = completion.Task;
            _activeDialog = dialogTask;
            _ = ShowAndCompleteAsync(owner, firstLaunch, completion);
            return dialogTask;
        }
    }

    private static async Task ShowAndCompleteAsync(
        Window? owner,
        bool firstLaunch,
        TaskCompletionSource<bool> completion)
    {
        bool setupCompleted = false;
        try
        {
            var permissions = new MacOSPermissionsView();
            var dialog = new FAContentDialog
            {
                Title = "Set up SnapX permissions",
                Content = new ScrollViewer { Content = permissions, MaxHeight = 520, MaxWidth = 560 },
                PrimaryButtonText = "Finish setup",
                IsPrimaryButtonEnabled = false,
                CloseButtonText = firstLaunch ? "Quit SnapX" : "Close",
                DefaultButton = FAContentDialogButton.Close
            };
            permissions.CompletionChanged += complete => dialog.IsPrimaryButtonEnabled = complete;
            FAContentDialogResult result;
            if (owner != null) { owner.Show(); owner.Activate(); result = await dialog.ShowAsync(owner); }
            else result = await dialog.ShowAsync();
            if (result != FAContentDialogResult.Primary ||
                !MacOSPermissions.HasScreenCaptureAccess()) return;
            SnapXL.Settings.MacOSPermissionSetupDismissed = true;
            // Persist completion before offering the next first-run step so a fast
            // quit/relaunch cannot reopen an already completed permission wizard.
            SettingManager.SaveAllSettings();
            setupCompleted = true;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Unable to show macOS permission setup");
        }
        finally
        {
            completion.TrySetResult(setupCompleted);
            lock (DialogLock)
            {
                if (ReferenceEquals(_activeDialog, completion.Task)) _activeDialog = null;
            }
        }
    }
}

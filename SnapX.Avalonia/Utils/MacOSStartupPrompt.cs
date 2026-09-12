using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

public static class MacOSStartupPrompt
{
    public const string RemovalInstructions = "You can turn this off here or remove SnapX from Open at Login in System Settings > General > Login Items & Extensions.";
    public static async Task OfferOnceAsync(Window owner)
    {
        if (!OperatingSystem.IsMacOS() || SnapXL.Portable) return;
        try
        {
            var service = new MacOSLoginItemService();
            var controller = new LoginItemController(service);
            if (!controller.ShouldOffer(SnapXL.Settings.MacOSLoginPromptDismissed)) return;
            var dialog = new FAContentDialog
            {
                Title = "Start SnapX when you log in?",
                Content = new TextBlock
                {
                    Text = "SnapX can be ready for captures whenever you sign in to your Mac. This is optional. macOS controls approval for login items.\n\n" + RemovalInstructions,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                },
                PrimaryButtonText = "Enable launch at login",
                CloseButtonText = "Not now",
                DefaultButton = FAContentDialogButton.Close
            };
            var choice = await dialog.ShowAsync(owner);
            SnapXL.Settings.MacOSLoginPromptDismissed = true;
            SettingManager.SaveApplicationConfigAsync();
            if (choice != FAContentDialogResult.Primary) return;
            var status = controller.ApplyChoice(true);
            if (status == LoginItemStatus.RequiresApproval) service.OpenSystemSettings();
            await ShowStatusAsync(owner, status);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Unable to configure launch at login");
            await ShowMessageAsync(owner, "Launch at login", ex.Message + "\n\nYou can try again in SnapX's Application settings.");
        }
    }

    public static string StatusText(LoginItemStatus status) => status switch
    {
        LoginItemStatus.Enabled => "On — SnapX will open when you log in.",
        LoginItemStatus.RequiresApproval => "Waiting for your approval in macOS Login Items settings.",
        LoginItemStatus.NotRegistered => "Off — SnapX will not start automatically.",
        LoginItemStatus.NotFound => "Not registered yet — enable launch at login to add SnapX.",
        _ => "Move SnapX to Applications to enable launch at login (macOS 13 or later)."
    };

    public static Task ShowStatusAsync(Window owner, LoginItemStatus status) =>
        ShowMessageAsync(owner, "Launch at login", StatusText(status) + "\n\n" + RemovalInstructions);

    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            CloseButtonText = "OK", DefaultButton = FAContentDialogButton.Close
        };
        await dialog.ShowAsync(owner);
    }
}

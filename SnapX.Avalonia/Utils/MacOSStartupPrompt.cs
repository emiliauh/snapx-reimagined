using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SnapX.Core;

namespace SnapX.Avalonia.Utils;

public static class MacOSStartupPrompt
{
    public const string RemovalInstructions = "To turn off this option, use this page. You can also remove SnapX from Open at Login in System Settings. Go to General, and then Login Items and Extensions.";
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
                Title = "Do you want SnapX to open when you log in?",
                Content = new TextBlock
                {
                    Text = "SnapX can be ready when you log in to your Mac. This option is not necessary. macOS controls login item approval.\n\n" + RemovalInstructions,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                },
                PrimaryButtonText = "Open SnapX at login",
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
            await ShowMessageAsync(owner, "Launch at login", ex.Message + "\n\nYou can try again in the Application settings in SnapX.");
        }
    }

    public static string StatusText(LoginItemStatus status) => status switch
    {
        LoginItemStatus.Enabled => "On. SnapX opens when you log in.",
        LoginItemStatus.RequiresApproval => "macOS needs your approval. Open Login Items in System Settings.",
        LoginItemStatus.NotRegistered => "Off. SnapX does not start automatically.",
        LoginItemStatus.NotFound => "SnapX is not registered. Select Open SnapX at login to register SnapX.",
        _ => "Move SnapX to the Applications folder. Launch at login needs macOS 13 or later."
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

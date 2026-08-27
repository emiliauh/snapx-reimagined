using Avalonia.Controls;
using SnapX.Avalonia.ViewModels.Settings;

namespace SnapX.Avalonia.Views.Settings;

public partial class ApplicationPathSettingsView : UserControl
{
    internal ApplicationPathSettingsVM ViewModel;
    public ApplicationPathSettingsView()
    {
        ViewModel = new ApplicationPathSettingsVM();
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null) ViewModel.SetStorageService(new StorageService(topLevel));
        };
    }
}

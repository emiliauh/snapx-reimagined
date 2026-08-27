using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.Views;

namespace SnapX.Avalonia.Views.Settings;

public partial class InAppSettingsHost : UserControl
{
    public InAppSettingsHost()
    {
        InitializeComponent();
    }

    private void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var mainViewModel = this.GetVisualAncestors()
            .OfType<MainView>()
            .Select(view => view.DataContext)
            .OfType<MainViewModel>()
            .FirstOrDefault();

        if (mainViewModel is null)
            return;

        mainViewModel.CurrentPage = Ioc.Default.GetRequiredService<HomePageViewModel>();
        mainViewModel.SelectedListItem = mainViewModel.Items
            .FirstOrDefault(item => item.ModelType == typeof(HomePageViewModel));
    }
}

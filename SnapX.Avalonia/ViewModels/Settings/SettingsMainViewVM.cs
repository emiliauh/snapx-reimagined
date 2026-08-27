using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using SnapX.Avalonia.ViewModels.Settings;
using SnapX.Core;
using SnapX.Core.Upload;

namespace SnapX.Avalonia.ViewModels;

public partial class SettingsMainViewVM : ViewModelBase
{
    [ObservableProperty]
    private bool _isPaneOpen = true;

    [ObservableProperty]
    private ViewModelBase _currentPage = new SettingsHomePageViewVM();
    private readonly Stack<ViewModelBase> _history = new();
    [ObservableProperty] private string _pageTitle = string.Empty;
    public bool CanGoBack => _history.Count > 0;
    private readonly Dictionary<string, Type> _pageFactory = [];

    public SettingsMainViewVM()
    {
        RegisterPage<SettingsHomePageViewVM>("Home");
        RegisterPage<CustomUploaderVM>("CustomUploader");
        RegisterPage<ImportExportVM>("ImportExport");
        RegisterPage<ScreenRecordOptionsVM>("ScreenRecordOptions");
        RegisterPage<DatabaseVM>("Database");
        RegisterPage<CoreUploaderVM>("BuiltInUploader");
        // SettingsCategoryVM is resolved on demand for every ordinary settings
        // tag. This keeps the navigation surface complete without a placeholder
        // page for each newly added ShareX option.
        RegisterPage<ApplicationPathSettingsVM>("Paths");

        foreach (var category in Enum.GetValues<UploaderCategory>())
        {
            var pageKey = category.ToString();

            RegisterPage<CoreUploaderVM>(pageKey);
        }
        foreach (var UploaderService in UploaderFactory.AllServices)
        {
            var pageKey = UploaderService.EnumValueObject.ToString();

            RegisterPage<CoreUploaderVM>("!" + pageKey);
        }
    }

    private void RegisterPage<T>(string key)
        where T : ViewModelBase
    {
        var type = typeof(T);

        // Primary key
        _pageFactory[key] = type;

        // Variants
        var variants = new[]
        {
            type.Name,
            key + "VM",
            key + "PageViewVM",
            key + "ViewModel"
        };

        foreach (var variant in variants)
        {
            _pageFactory[variant] = type;
        }
    }
    public void Navigate(string? categoryTag, string destinationTag)
    {
        DebugHelper.WriteLine("SettingsMainViewVM.Navigate: " + destinationTag);
        if (_pageFactory.TryGetValue(destinationTag, out var factory))
        {
            var type = factory;
            DebugHelper.WriteLine(type.ToString());
            var vm = Design.IsDesignMode
                ? Activator.CreateInstance(type)
                : Ioc.Default.GetService(type);
            if (vm is not ViewModelBase vmb)
            {
                DebugHelper.WriteLine($"Can't get ViewModelBase on {type}. Did you register it in ViewLocator & IoC?");
                return;
            }
            if (destinationTag.StartsWith("!"))
            {
                var uploaderName = destinationTag.TrimStart('!');

                var foundCategory = Enum.Parse<UploaderCategory>(categoryTag);

                if (vmb is CoreUploaderVM coreVM)
                {
                    coreVM.NavigateToCategory(foundCategory);
                    coreVM.SelectUploader(uploaderName);
                }
            }
            if (Enum.TryParse<UploaderCategory>(destinationTag, out var category))
            {
                if (vmb is CoreUploaderVM coreVM)
                {
                    coreVM.NavigateToCategory(category);
                }
            }

            _history.Push(CurrentPage);
            PageTitle = $"Settings for {Core.SnapXL.AppName} : {categoryTag ?? destinationTag}";
            CurrentPage = vmb;
        }
        else
        {
            var categoryVM = Design.IsDesignMode
                ? new SettingsCategoryVM()
                : Ioc.Default.GetService<SettingsCategoryVM>();

            if (categoryVM is null)
            {
                DebugHelper.WriteLine("SettingsMainViewVM.Navigate: SettingsCategoryVM is not registered.");
                return;
            }

            categoryVM.Configure(categoryTag, destinationTag);
            _history.Push(CurrentPage);
            PageTitle = $"Settings for {Core.SnapXL.AppName} : {categoryVM.PageTitle}";
            CurrentPage = categoryVM;
        }
    }
    public bool TryGetPage(string tag, out Type type)
    {
        return _pageFactory.TryGetValue(tag, out type);
    }
    public void Back()
    {
        if (!CanGoBack)
            return;

        CurrentPage = _history.Pop();
    }
}

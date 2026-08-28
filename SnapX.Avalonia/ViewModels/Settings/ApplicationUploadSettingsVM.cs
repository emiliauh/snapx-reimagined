using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SnapX.Core;

namespace SnapX.Avalonia.ViewModels.Settings;

/// <summary>
/// Edits upload options which apply to the complete application.
/// </summary>
public partial class ApplicationUploadSettingsVM : ViewModelBase
{
    private readonly ApplicationConfig _config;
    private string _maxUploadFailRetryText;
    private string _largeFileWarningText;
    private string _uploadLimitText;
    private string _validationMessage = string.Empty;

    public ApplicationUploadSettingsVM()
    {
        // Use an isolated object for the design preview. Runtime views use the
        // configuration loaded by SnapXL before the settings window opens.
        _config = SnapXL.Settings ?? new ApplicationConfig();
        _maxUploadFailRetryText = _config.MaxUploadFailRetry.ToString(CultureInfo.InvariantCulture);
        _largeFileWarningText = _config.ShowLargeFileSizeWarning.ToString(CultureInfo.InvariantCulture);
        _uploadLimitText = _config.UploadLimit.ToString(CultureInfo.InvariantCulture);
    }

    public bool DisableUpload
    {
        get => _config.DisableUpload;
        set
        {
            if (_config.DisableUpload == value) return;
            _config.DisableUpload = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool UrlEncodeIgnoreEmoji
    {
        get => _config.URLEncodeIgnoreEmoji;
        set
        {
            if (_config.URLEncodeIgnoreEmoji == value) return;
            _config.URLEncodeIgnoreEmoji = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool ShowUploadWarning
    {
        get => _config.ShowUploadWarning;
        set
        {
            if (_config.ShowUploadWarning == value) return;
            _config.ShowUploadWarning = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool ShowMultiUploadWarning
    {
        get => _config.ShowMultiUploadWarning;
        set
        {
            if (_config.ShowMultiUploadWarning == value) return;
            _config.ShowMultiUploadWarning = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string MaxUploadFailRetryText
    {
        get => _maxUploadFailRetryText;
        set => SetProperty(ref _maxUploadFailRetryText, value);
    }

    public string LargeFileWarningText
    {
        get => _largeFileWarningText;
        set => SetProperty(ref _largeFileWarningText, value);
    }

    public string UploadLimitText
    {
        get => _uploadLimitText;
        set => SetProperty(ref _uploadLimitText, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (!TryReadInteger(MaxUploadFailRetryText, "Retry count", 0, 100, out var retryCount) ||
            !TryReadInteger(LargeFileWarningText, "Large-file warning", 0, 1_000_000, out var warningSize) ||
            !TryReadInteger(UploadLimitText, "Upload limit", 1, 1000, out var uploadLimit))
        {
            return;
        }

        _config.MaxUploadFailRetry = retryCount;
        _config.ShowLargeFileSizeWarning = warningSize;
        _config.UploadLimit = uploadLimit;
        Save();
        ValidationMessage = "Upload settings saved.";
    }

    private bool TryReadInteger(string text, string label, int minimum, int maximum, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
        {
            ValidationMessage = string.Empty;
            return true;
        }

        ValidationMessage = $"{label} must be a whole number from {minimum} to {maximum}.";
        return false;
    }

    private void Save() => _config.SaveAsync();
}

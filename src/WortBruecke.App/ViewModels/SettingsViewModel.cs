using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed record LayoutStatusViewModel(string CultureCode, string Name, bool IsInstalled)
{
    public string Status => IsInstalled ? "Установлена" : "Не установлена";
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly Action<AppSettings> _applySettings;
    private PassageModeOption? _selectedPassageMode;
    private string _apiModel = "gpt-5-mini";
    private string _apiKey = string.Empty;
    private bool _allowOnlineLanguageAnalysis;
    private bool _useDarkTheme;
    private string _saveStatus = string.Empty;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        IKeyboardLayoutService keyboardLayoutService,
        Action<AppSettings> applySettings)
    {
        _settingsStore = settingsStore;
        _keyboardLayoutService = keyboardLayoutService;
        _applySettings = applySettings;
        PassageModes =
        [
            new PassageModeOption(PassagePracticeMode.Translation, "Перевод по предложениям", "RU → DE"),
            new PassageModeOption(PassagePracticeMode.GermanTyping, "Чистый набор немецкого", "DE → DE")
        ];
        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => !string.IsNullOrWhiteSpace(ApiModel),
            error => SaveStatus = error.UserMessage);
        RefreshLayoutsCommand = new RelayCommand(RefreshLayouts);
        OpenWindowsSettingsCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }));
    }

    public ObservableCollection<PassageModeOption> PassageModes { get; }
    public ObservableCollection<LayoutStatusViewModel> LayoutStatuses { get; } = [];
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand RefreshLayoutsCommand { get; }
    public RelayCommand OpenWindowsSettingsCommand { get; }

    public PassageModeOption? SelectedPassageMode
    {
        get => _selectedPassageMode;
        set => SetProperty(ref _selectedPassageMode, value);
    }

    public string ApiModel
    {
        get => _apiModel;
        set
        {
            if (SetProperty(ref _apiModel, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public bool AllowOnlineLanguageAnalysis
    {
        get => _allowOnlineLanguageAnalysis;
        set => SetProperty(ref _allowOnlineLanguageAnalysis, value);
    }

    public bool UseDarkTheme
    {
        get => _useDarkTheme;
        set
        {
            if (SetProperty(ref _useDarkTheme, value))
            {
                ThemeManager.Apply(value);
            }
        }
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set => SetProperty(ref _saveStatus, value);
    }

    public string VersionText => $"LernType {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.1.0"}";

    public async Task InitializeAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        SelectedPassageMode = PassageModes.First(mode => mode.Mode == settings.PassageMode);
        ApiModel = settings.ApiModel;
        ApiKey = settings.ApiKey;
        AllowOnlineLanguageAnalysis = settings.AllowOnlineLanguageAnalysis;
        _useDarkTheme = settings.UseDarkTheme;
        OnPropertyChanged(nameof(UseDarkTheme));
        RefreshLayouts();
        _applySettings(settings);
    }

    private async Task SaveAsync()
    {
        var settings = new AppSettings
        {
            SourceCulture = LanguagePair.RussianToGerman.Source.CultureCode,
            TargetCulture = LanguagePair.RussianToGerman.Target.CultureCode,
            PassageMode = SelectedPassageMode?.Mode ?? PassagePracticeMode.Translation,
            ApiModel = ApiModel.Trim(),
            ApiKey = ApiKey.Trim(),
            AllowOnlineLanguageAnalysis = AllowOnlineLanguageAnalysis,
            UseDarkTheme = UseDarkTheme
        };
        await _settingsStore.SaveAsync(settings);
        _applySettings(settings);
        SaveStatus = $"Сохранено {DateTime.Now:HH:mm}";
    }

    private void RefreshLayouts()
    {
        var pair = LanguagePair.RussianToGerman;
        var availability = _keyboardLayoutService.CheckInstalled(pair.Source.CultureCode, pair.Target.CultureCode);
        LayoutStatuses.Clear();
        foreach (var item in availability)
        {
            var name = item.CultureCode == pair.Source.CultureCode ? "Русская раскладка" : "Немецкая раскладка";
            LayoutStatuses.Add(new LayoutStatusViewModel(item.CultureCode, name, item.IsInstalled));
        }
    }
}

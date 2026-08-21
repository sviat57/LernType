using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed class TelcViewModel : ObservableObject
{
    private readonly ILanguageAnalysisService _analysisService;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly LanguagePair _pair = LanguagePair.RussianToGerman;
    private string _inputText = string.Empty;
    private string _level = string.Empty;
    private string _confidenceText = string.Empty;
    private string _summary = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasResult;
    private bool _hasError;
    private bool _isBusy;

    public TelcViewModel(ILanguageAnalysisService analysisService, IKeyboardLayoutService keyboardLayoutService)
    {
        _analysisService = analysisService;
        _keyboardLayoutService = keyboardLayoutService;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy && InputText.Trim().Length >= 20);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public ObservableCollection<string> Strengths { get; } = [];
    public ObservableCollection<TelcError> Errors { get; } = [];
    public AsyncRelayCommand AnalyzeCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CharacterHint));
            }
        }
    }

    public string Level { get => _level; private set => SetProperty(ref _level, value); }
    public string ConfidenceText { get => _confidenceText; private set => SetProperty(ref _confidenceText, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(AnalyzeButtonText));
            }
        }
    }

    public string CharacterHint => InputText.Trim().Length < 20 ? $"Ещё минимум {20 - InputText.Trim().Length} симв." : $"{InputText.Length} симв.";
    public string AnalyzeButtonText => IsBusy ? "Анализируем…" : "Определить уровень";

    public void Activate() => _keyboardLayoutService.SwitchTo(_pair.Target.CultureCode);

    public void CancelOnlineAnalysis() => AnalyzeCommand.Cancel();

    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        HasError = false;
        HasResult = false;
        try
        {
            var result = await _analysisService.AnalyzeTelcAsync(InputText, cancellationToken);
            Level = result.Level;
            ConfidenceText = $"Уверенность {result.Confidence:P0}";
            Summary = result.Summary;
            Strengths.Clear();
            Errors.Clear();
            foreach (var strength in result.Strengths)
            {
                Strengths.Add(strength);
            }
            foreach (var error in result.Errors)
            {
                Errors.Add(error);
            }
            HasResult = true;
        }
        catch (LanguageAnalysisUnavailableException exception)
        {
            ErrorMessage = exception.Message;
            HasError = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "Онлайн-анализ отменён.";
            HasError = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            ErrorMessage = "Не удалось получить анализ. Проверьте сеть и повторите попытку.";
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character)
        {
            InputText += character;
        }
    }
}

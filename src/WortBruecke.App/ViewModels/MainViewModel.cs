using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;
using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TrainerViewModel _trainer;
    private readonly LearningPathViewModel _learningPath;
    private readonly ExamCenterViewModel _examCenter;
    private readonly TextPracticeViewModel _texts;
    private readonly BookViewModel _books;
    private readonly VocabularyTestViewModel _vocabularyTest;
    private readonly GrammarViewModel _grammar;
    private readonly TelcViewModel _telc;
    private readonly SettingsViewModel _settings;
    private readonly AudioPracticeViewModel? _audio;
    private readonly ProgressViewModel? _progress;
    private readonly Dictionary<string, object> _screens;
    private readonly Dictionary<string, string> _titles;
    private readonly Dictionary<string, Func<CancellationToken, Task>> _initializers;
    private readonly Dictionary<string, Task> _initializationTasks = [];
    private readonly HashSet<string> _initializedScreens = new(StringComparer.Ordinal) { "home" };
    private readonly object _initializationSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private object _currentViewModel;
    private string _currentKey = "home";
    private string _currentTitle = "Сегодня";
    private string _shellStatus = "Подготавливаем локальное хранилище…";
    private string _shellErrorMessage = string.Empty;
    private string _shellTechnicalCode = string.Empty;
    private bool _isShellBusy = true;
    private bool _isStorageReady;

    public MainViewModel(
        IContentRepository contentRepository,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IImageProvider imageProvider,
        ILanguageAnalysisService languageAnalysisService,
        ISettingsStore settingsStore,
        IBookRepository bookRepository,
        IBookVocabularyExtractor bookVocabularyExtractor,
        IOfflineDictionaryService offlineDictionaryService,
        ILearningProgressRepository learningProgressRepository,
        IExamBlueprintRepository examBlueprintRepository,
        IAttemptRepository? attemptRepository = null,
        IReviewStateRepository? reviewStateRepository = null,
        IAudioPracticeService? audioPracticeService = null,
        TemporaryAudioRecordingStore? temporaryAudioRecordingStore = null)
    {
        ArgumentNullException.ThrowIfNull(contentRepository);
        ArgumentNullException.ThrowIfNull(progressRepository);
        ArgumentNullException.ThrowIfNull(keyboardLayoutService);
        ArgumentNullException.ThrowIfNull(imageProvider);
        ArgumentNullException.ThrowIfNull(languageAnalysisService);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(bookRepository);
        ArgumentNullException.ThrowIfNull(bookVocabularyExtractor);
        ArgumentNullException.ThrowIfNull(offlineDictionaryService);
        ArgumentNullException.ThrowIfNull(learningProgressRepository);
        ArgumentNullException.ThrowIfNull(examBlueprintRepository);

        var home = new HomeViewModel(Navigate);
        _learningPath = attemptRepository is null
            ? new LearningPathViewModel(contentRepository, progressRepository, learningProgressRepository, examBlueprintRepository, Navigate)
            : new LearningPathViewModel(contentRepository, attemptRepository, examBlueprintRepository, Navigate);
        _examCenter = attemptRepository is null
            ? new ExamCenterViewModel(examBlueprintRepository, learningProgressRepository, Navigate)
            : new ExamCenterViewModel(examBlueprintRepository, attemptRepository, Navigate);
        _trainer = attemptRepository is null
            ? new TrainerViewModel(contentRepository, progressRepository, keyboardLayoutService, imageProvider)
            : new TrainerViewModel(contentRepository, attemptRepository, keyboardLayoutService, imageProvider, reviewStateRepository);
        _texts = attemptRepository is null
            ? new TextPracticeViewModel(contentRepository, progressRepository, keyboardLayoutService)
            : new TextPracticeViewModel(contentRepository, attemptRepository, keyboardLayoutService);
        _books = attemptRepository is null
            ? new BookViewModel(bookRepository, bookVocabularyExtractor, progressRepository, keyboardLayoutService, offlineDictionaryService)
            : new BookViewModel(bookRepository, bookVocabularyExtractor, attemptRepository, keyboardLayoutService, offlineDictionaryService);
        _vocabularyTest = attemptRepository is null
            ? new VocabularyTestViewModel(contentRepository, progressRepository, keyboardLayoutService)
            : new VocabularyTestViewModel(contentRepository, attemptRepository, keyboardLayoutService);
        _grammar = attemptRepository is null
            ? new GrammarViewModel(contentRepository, progressRepository, keyboardLayoutService, new GrammarHeuristicService(), languageAnalysisService)
            : new GrammarViewModel(contentRepository, attemptRepository, keyboardLayoutService, new GrammarHeuristicService(), languageAnalysisService);
        _telc = new TelcViewModel(languageAnalysisService, keyboardLayoutService);
        _settings = new SettingsViewModel(settingsStore, keyboardLayoutService, ApplySettings);
        if (attemptRepository is not null && reviewStateRepository is not null)
        {
            _progress = new ProgressViewModel(attemptRepository, reviewStateRepository, navigate: Navigate);
        }
        if (audioPracticeService is not null)
        {
            _audio = new AudioPracticeViewModel(
                audioPracticeService,
                attemptRepository,
                recordingStore: temporaryAudioRecordingStore);
        }

        _trainer.PassageExerciseRequested += (_, _) =>
            ObserveNavigation(OpenTextPracticeAsync(level: null, suggested: true));
        _trainer.TextPracticeRequested += level =>
            ObserveNavigation(OpenTextPracticeAsync(level, suggested: false));
        _texts.SuggestedPracticeCompleted += (_, _) => Navigate("trainer");

        _screens = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["home"] = home,
            ["path"] = _learningPath,
            ["exams"] = _examCenter,
            ["trainer"] = _trainer,
            ["texts"] = _texts,
            ["books"] = _books,
            ["test"] = _vocabularyTest,
            ["grammar"] = _grammar,
            ["telc"] = _telc,
            ["settings"] = _settings
        };
        if (_audio is not null) _screens["audio"] = _audio;
        if (_progress is not null) _screens["progress"] = _progress;

        _titles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["home"] = "Сегодня",
            ["path"] = "Учебный путь",
            ["exams"] = "Экзаменационный центр",
            ["trainer"] = "Практика",
            ["texts"] = "Тексты",
            ["books"] = "Личная библиотека",
            ["test"] = "Диагностика словаря",
            ["grammar"] = "Грамматика",
            ["telc"] = "Анализ письменной речи",
            ["audio"] = "Аудирование и речь",
            ["progress"] = "Мой прогресс",
            ["settings"] = "Настройки"
        };
        _initializers = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["path"] = _ => _learningPath.InitializeAsync(),
            ["exams"] = _ => _examCenter.InitializeAsync(),
            ["trainer"] = _ => _trainer.InitializeAsync(),
            ["texts"] = _ => _texts.InitializeAsync(),
            ["books"] = token => _books.InitializeAsync(token),
            ["test"] = token => _vocabularyTest.InitializeAsync(token),
            ["grammar"] = _ => _grammar.InitializeAsync(),
            ["settings"] = _ => _settings.InitializeAsync()
        };
        if (_audio is not null) _initializers["audio"] = _ => _audio.InitializeAsync();
        if (_progress is not null) _initializers["progress"] = _ => _progress.InitializeAsync();

        _currentViewModel = home;
        RetryCommand = new AsyncRelayCommand(RetryAsync, onError: SetShellError);
        DismissErrorCommand = new RelayCommand(ClearShellError, () => HasShellError);
        NavigationItems =
        [
            CreateNav("home", "Сегодня", "Icon.Home"),
            CreateNav("path", "Путь Pre-A1–C2", "Icon.Route"),
            CreateNav("trainer", "Практика", "Icon.Cards"),
            .. (_audio is null ? [] : new[] { CreateNav("audio", "Слушать и говорить", "Icon.Audio") }),
            CreateNav("exams", "Экзамены", "Icon.Certificate"),
            CreateNav("books", "Моя библиотека", "Icon.Document"),
            .. (_progress is null ? [] : new[] { CreateNav("progress", "Прогресс", "Icon.Progress") }),
            CreateNav("settings", "Настройки", "Icon.Settings")
        ];
        SetSelection("home");
    }

    public event Func<CancellationToken, Task>? StartupRetryRequested;
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public RelayCommand DismissErrorCommand { get; }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => SetProperty(ref _currentTitle, value);
    }

    public string ShellStatus
    {
        get => _shellStatus;
        private set => SetProperty(ref _shellStatus, value);
    }

    public string ShellErrorMessage
    {
        get => _shellErrorMessage;
        private set => SetProperty(ref _shellErrorMessage, value);
    }

    public string ShellTechnicalCode
    {
        get => _shellTechnicalCode;
        private set => SetProperty(ref _shellTechnicalCode, value);
    }

    public bool IsShellBusy
    {
        get => _isShellBusy;
        private set => SetProperty(ref _isShellBusy, value);
    }

    public bool IsStorageReady
    {
        get => _isStorageReady;
        private set => SetProperty(ref _isStorageReady, value);
    }

    public bool HasShellError => !string.IsNullOrWhiteSpace(ShellErrorMessage);
    public string ReadinessLabel => IsStorageReady ? "Локальные данные готовы" : "Подготовка данных";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsShellBusy = true;
        ShellStatus = "Загружаем локальные настройки…";
        try
        {
            await EnsureInitializedAsync("settings", cancellationToken);
            ClearShellError();
            ShellStatus = "Готово к автономной работе";
        }
        finally
        {
            IsShellBusy = false;
        }
    }

    public void MarkStorageReady()
    {
        IsStorageReady = true;
        ShellStatus = "Локальные данные готовы";
        OnPropertyChanged(nameof(ReadinessLabel));
    }

    public void ReportStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        IsStorageReady = false;
        IsShellBusy = false;
        ShellStatus = "Локальное хранилище требует внимания";
        OnPropertyChanged(nameof(ReadinessLabel));
        SetShellError(OperationError.FromException(
            exception,
            "Не удалось открыть локальные данные. Исходные файлы сохранены; повторите проверку."));
    }

    public void ReportUnhandledFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SetShellError(OperationError.FromException(
            exception,
            "Операция завершилась с ошибкой. Данные сохранены; повторите действие."));
    }

    public void Navigate(string key) => ObserveNavigation(NavigateAsync(key));

    public async Task NavigateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_screens.TryGetValue(key, out var screen))
        {
            SetShellError(new OperationError(OperationErrorKind.Validation, "Этот раздел пока недоступен.", "UnknownRoute"));
            return;
        }
        if (!IsStorageReady && key is not ("home" or "settings"))
        {
            SetShellError(new OperationError(
                OperationErrorKind.StorageUnavailable,
                "Сначала завершите проверку локального хранилища.",
                "StorageNotReady"));
            return;
        }

        CancelOutgoingWork();
        if (key == "texts") _texts.ApplyLevelFilter(null);
        _currentKey = key;
        CurrentViewModel = screen;
        CurrentTitle = _titles[key];
        SetSelection(key);
        ClearShellError();
        IsShellBusy = true;
        ShellStatus = $"Открываем: {_titles[key]}…";
        try
        {
            var initializedNow = await EnsureInitializedAsync(key, cancellationToken);
            if (string.Equals(_currentKey, key, StringComparison.Ordinal))
            {
                await ActivateAsync(key, initializedNow);
                ShellStatus = "Готово к автономной работе";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            ShellStatus = "Операция отменена";
        }
        catch (Exception exception)
        {
            SetShellError(OperationError.FromException(exception, $"Раздел «{_titles[key]}» не загрузился. Повторите попытку."));
        }
        finally
        {
            if (string.Equals(_currentKey, key, StringComparison.Ordinal)) IsShellBusy = false;
        }
    }

    private async Task OpenTextPracticeAsync(string? level, bool suggested)
    {
        if (!IsStorageReady)
        {
            SetShellError(new OperationError(OperationErrorKind.StorageUnavailable, "Локальные данные ещё не готовы.", "StorageNotReady"));
            return;
        }
        CancelOutgoingWork();
        _currentKey = "texts";
        CurrentViewModel = _texts;
        CurrentTitle = _titles["texts"];
        SetSelection("texts");
        IsShellBusy = true;
        ShellStatus = "Готовим текстовую практику…";
        try
        {
            await EnsureInitializedAsync("texts", _lifetime.Token);
            if (suggested) _texts.StartSuggested();
            else _texts.ApplyLevelFilter(level);
            ShellStatus = "Готово к автономной работе";
        }
        catch (Exception exception)
        {
            SetShellError(OperationError.FromException(exception, "Текстовая практика не загрузилась."));
        }
        finally
        {
            IsShellBusy = false;
        }
    }

    private Task<bool> EnsureInitializedAsync(string key, CancellationToken cancellationToken)
    {
        if (!_initializers.ContainsKey(key)) return Task.FromResult(false);

        Task task;
        var created = false;
        lock (_initializationSync)
        {
            if (_initializedScreens.Contains(key)) return Task.FromResult(false);
            if (!_initializationTasks.TryGetValue(key, out task!))
            {
                task = InitializeScreenCoreAsync(key);
                _initializationTasks[key] = task;
                created = true;
            }
        }
        return AwaitInitializationAsync(task, key, created, cancellationToken);
    }

    private async Task InitializeScreenCoreAsync(string key)
    {
        await _initializers[key](_lifetime.Token);
    }

    private async Task<bool> AwaitInitializationAsync(
        Task task,
        string key,
        bool created,
        CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken);
            lock (_initializationSync)
            {
                _initializedScreens.Add(key);
                _initializationTasks.Remove(key);
            }
            return created;
        }
        catch
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                lock (_initializationSync) _initializationTasks.Remove(key);
            }
            throw;
        }
    }

    private async Task ActivateAsync(string key, bool initializedNow)
    {
        switch (key)
        {
            case "grammar":
                _grammar.Activate();
                break;
            case "telc":
                _telc.Activate();
                break;
            case "test":
                _vocabularyTest.Activate();
                break;
            case "path" when !initializedNow:
                await _learningPath.ActivateAsync();
                break;
            case "exams" when !initializedNow:
                await _examCenter.ActivateAsync();
                break;
            case "progress" when !initializedNow && _progress is not null:
                await _progress.RefreshCommand.ExecuteAsync();
                break;
            case "audio" when _audio is not null:
                _audio.Activate();
                break;
        }
    }

    private void CancelOutgoingWork()
    {
        if (ReferenceEquals(CurrentViewModel, _books)) _books.CancelPendingOperations();
        if (ReferenceEquals(CurrentViewModel, _grammar)) _grammar.CancelOnlineAnalysis();
        if (ReferenceEquals(CurrentViewModel, _telc)) _telc.CancelOnlineAnalysis();
        if (_audio is not null && ReferenceEquals(CurrentViewModel, _audio)) _audio.CancelPendingOperations();
    }

    private void ApplySettings(AppSettings settings)
    {
        _trainer.ApplySettings(settings);
        _texts.ApplySettings(settings);
        ThemeManager.Apply(settings.UseDarkTheme);
    }

    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        ClearShellError();
        if (!IsStorageReady)
        {
            var handler = StartupRetryRequested;
            if (handler is not null) await handler(cancellationToken);
            return;
        }

        lock (_initializationSync)
        {
            _initializedScreens.Remove(_currentKey);
            _initializationTasks.Remove(_currentKey);
        }
        await NavigateAsync(_currentKey, cancellationToken);
    }

    private void ObserveNavigation(Task task) => _ = ObserveNavigationCoreAsync(task);

    private async Task ObserveNavigationCoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application shutdown owns this cancellation.
        }
        catch (Exception exception)
        {
            ReportUnhandledFailure(exception);
        }
    }

    private void SetShellError(OperationError error)
    {
        ShellErrorMessage = error.UserMessage;
        ShellTechnicalCode = error.TechnicalCode;
        OnPropertyChanged(nameof(HasShellError));
        DismissErrorCommand.RaiseCanExecuteChanged();
    }

    private void ClearShellError()
    {
        ShellErrorMessage = string.Empty;
        ShellTechnicalCode = string.Empty;
        OnPropertyChanged(nameof(HasShellError));
        DismissErrorCommand.RaiseCanExecuteChanged();
    }

    private NavigationItemViewModel CreateNav(string key, string title, string iconResource)
    {
        var icon = Application.Current.TryFindResource(iconResource) as Geometry ?? Geometry.Empty;
        return new NavigationItemViewModel(key, title, icon, new RelayCommand(() => Navigate(key)));
    }

    private void SetSelection(string key)
    {
        foreach (var item in NavigationItems) item.IsSelected = item.Key == key;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _books.CancelPendingOperations();
        _grammar.CancelOnlineAnalysis();
        _telc.CancelOnlineAnalysis();
        if (_audio is not null) await _audio.DisposeAsync();
        _lifetime.Dispose();
    }
}

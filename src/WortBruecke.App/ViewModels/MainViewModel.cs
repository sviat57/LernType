using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TrainerViewModel _trainer;
    private readonly CoursePathViewModel _coursePath;
    private readonly CourseLessonViewModel _courseLesson;
    private readonly InteractiveExercisesViewModel _interactiveExercises;
    private readonly TextPracticeViewModel _texts;
    private readonly VocabularyTestViewModel _vocabularyTest;
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
    private long _navigationGeneration;
    private object _currentViewModel;
    private string _currentKey = "home";
    private string _currentTitle = "Сегодня";
    private string _shellStatus = "Подготавливаем локальное хранилище…";
    private string _shellErrorMessage = string.Empty;
    private string _shellTechnicalCode = string.Empty;
    private bool _isShellBusy = true;
    private bool _isStorageReady;
    private CourseLessonLaunch? _lastCourseLessonLaunch;
    private CourseExamLaunch? _lastCourseExamLaunch;

    public MainViewModel(
        IContentRepository contentRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IImageProvider imageProvider,
        ISettingsStore settingsStore,
        ICourseCatalogRepository courseCatalogRepository,
        ICourseProgressRepository courseProgressRepository,
        IAttemptRepository attemptRepository,
        IReviewStateRepository? reviewStateRepository = null,
        IAudioPracticeService? audioPracticeService = null,
        TemporaryAudioRecordingStore? temporaryAudioRecordingStore = null)
    {
        ArgumentNullException.ThrowIfNull(contentRepository);
        ArgumentNullException.ThrowIfNull(keyboardLayoutService);
        ArgumentNullException.ThrowIfNull(imageProvider);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(courseCatalogRepository);
        ArgumentNullException.ThrowIfNull(courseProgressRepository);
        ArgumentNullException.ThrowIfNull(attemptRepository);

        var home = new HomeViewModel(Navigate);
        _interactiveExercises = new InteractiveExercisesViewModel(Navigate);
        _coursePath = new CoursePathViewModel(
            courseCatalogRepository,
            courseProgressRepository,
            launch => OpenCourseLessonAsync(launch),
            launch => OpenCourseExamAsync(launch));
        _courseLesson = new CourseLessonViewModel(
            courseCatalogRepository,
            courseProgressRepository,
            attemptRepository,
            () => Navigate("path"),
            audioPracticeService,
            temporaryAudioRecordingStore);
        _trainer = new TrainerViewModel(contentRepository, attemptRepository, keyboardLayoutService, imageProvider, reviewStateRepository);
        _texts = new TextPracticeViewModel(contentRepository, attemptRepository, keyboardLayoutService);
        _vocabularyTest = new VocabularyTestViewModel(contentRepository, attemptRepository, keyboardLayoutService);
        _settings = new SettingsViewModel(settingsStore, keyboardLayoutService, ApplySettings);
        if (reviewStateRepository is not null)
        {
            _progress = new ProgressViewModel(
                courseCatalogRepository,
                courseProgressRepository,
                attemptRepository,
                reviewStateRepository,
                navigate: Navigate);
        }
        if (audioPracticeService is not null)
        {
            _audio = new AudioPracticeViewModel(
                audioPracticeService,
                attemptRepository,
                recordingStore: temporaryAudioRecordingStore);
        }

        _trainer.TextPracticeRequested += level =>
            ObserveNavigation(OpenTextPracticeAsync(level));

        _screens = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["home"] = home,
            ["path"] = _coursePath,
            ["course-lesson"] = _courseLesson,
            ["interactive"] = _interactiveExercises,
            ["trainer"] = _trainer,
            ["texts"] = _texts,
            ["test"] = _vocabularyTest,
            ["settings"] = _settings
        };
        if (_audio is not null) _screens["audio"] = _audio;
        if (_progress is not null) _screens["progress"] = _progress;

        _titles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["home"] = "Сегодня",
            ["path"] = "Курсы",
            ["course-lesson"] = "Урок курса",
            ["interactive"] = "Интерактивные упражнения",
            ["trainer"] = "Слова и предложения",
            ["texts"] = "Набор текстов",
            ["test"] = "Двусторонний словарный тест",
            ["audio"] = "Аудирование и речь",
            ["progress"] = "Мой прогресс",
            ["settings"] = "Настройки"
        };
        _initializers = new Dictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["path"] = token => _coursePath.InitializeAsync(token),
            ["trainer"] = _ => _trainer.InitializeAsync(),
            ["texts"] = _ => _texts.InitializeAsync(),
            ["test"] = token => _vocabularyTest.InitializeAsync(token),
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
            CreateNav("path", "Курсы", "Icon.Route"),
            CreateNav("interactive", "Интерактивные упражнения", "Icon.Cards"),
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

        var navigation = BeginNavigation();
        await CancelOutgoingWorkAsync();
        if (key == "texts") _texts.ApplyLevelFilter(null);
        if (key == "trainer") _trainer.ClearLevelContext();
        if (key == "audio" && _audio is not null) _audio.ApplyLevelFilter(null);
        _currentKey = key;
        CurrentViewModel = screen;
        CurrentTitle = _titles[key];
        SetSelection(key is "trainer" or "texts" or "test" or "audio" ? "interactive" : key);
        ClearShellError();
        IsShellBusy = true;
        ShellStatus = $"Открываем: {_titles[key]}…";
        try
        {
            var initializedNow = await EnsureInitializedAsync(key, cancellationToken);
            if (IsCurrentNavigation(navigation))
            {
                await ActivateAsync(key, initializedNow);
                if (IsCurrentNavigation(navigation))
                {
                    ShellStatus = "Готово к автономной работе";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            if (IsCurrentNavigation(navigation))
            {
                ShellStatus = "Операция отменена";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(navigation))
            {
                SetShellError(OperationError.FromException(exception, $"Раздел «{_titles[key]}» не загрузился. Повторите попытку."));
            }
        }
        finally
        {
            if (IsCurrentNavigation(navigation)) IsShellBusy = false;
        }
    }

    private async Task OpenCourseLessonAsync(CourseLessonLaunch launch)
    {
        if (!IsStorageReady)
        {
            SetShellError(new OperationError(OperationErrorKind.StorageUnavailable, "Локальные данные ещё не готовы.", "StorageNotReady"));
            return;
        }

        var navigation = BeginNavigation();
        await CancelOutgoingWorkAsync();
        _lastCourseLessonLaunch = launch;
        _lastCourseExamLaunch = null;
        _currentKey = "course-lesson";
        CurrentViewModel = _courseLesson;
        CurrentTitle = "Урок курса";
        SetSelection("path");
        ClearShellError();
        IsShellBusy = true;
        ShellStatus = "Открываем урок…";
        try
        {
            await _courseLesson.PrepareAsync(launch, _lifetime.Token);
            if (IsCurrentNavigation(navigation))
            {
                CurrentTitle = _courseLesson.FlowTitle;
                ShellStatus = "Урок готов офлайн";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(navigation))
            {
                SetShellError(OperationError.FromException(exception, "Урок не загрузился. Проверьте локальный каталог и повторите попытку."));
            }
        }
        finally
        {
            if (IsCurrentNavigation(navigation)) IsShellBusy = false;
        }
    }

    private async Task OpenCourseExamAsync(CourseExamLaunch launch)
    {
        if (!IsStorageReady)
        {
            SetShellError(new OperationError(OperationErrorKind.StorageUnavailable, "Локальные данные ещё не готовы.", "StorageNotReady"));
            return;
        }

        var navigation = BeginNavigation();
        await CancelOutgoingWorkAsync();
        _lastCourseLessonLaunch = null;
        _lastCourseExamLaunch = launch;
        _currentKey = "course-lesson";
        CurrentViewModel = _courseLesson;
        CurrentTitle = "Внутренний экзамен";
        SetSelection("path");
        ClearShellError();
        IsShellBusy = true;
        ShellStatus = "Подготавливаем внутренний экзамен…";
        try
        {
            await _courseLesson.PrepareExamAsync(launch, _lifetime.Token);
            if (IsCurrentNavigation(navigation))
            {
                CurrentTitle = _courseLesson.FlowTitle;
                ShellStatus = "Экзамен готов офлайн";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(navigation))
            {
                SetShellError(OperationError.FromException(exception, "Внутренний экзамен не загрузился."));
            }
        }
        finally
        {
            if (IsCurrentNavigation(navigation)) IsShellBusy = false;
        }
    }

    private async Task OpenTextPracticeAsync(string? level)
    {
        if (!IsStorageReady)
        {
            SetShellError(new OperationError(OperationErrorKind.StorageUnavailable, "Локальные данные ещё не готовы.", "StorageNotReady"));
            return;
        }
        var navigation = BeginNavigation();
        await CancelOutgoingWorkAsync();
        _currentKey = "texts";
        CurrentViewModel = _texts;
        CurrentTitle = _titles["texts"];
        SetSelection("interactive");
        IsShellBusy = true;
        ShellStatus = "Готовим текстовую практику…";
        try
        {
            await EnsureInitializedAsync("texts", _lifetime.Token);
            if (!IsCurrentNavigation(navigation)) return;
            _texts.ApplyLevelFilter(level);
            ShellStatus = "Готово к автономной работе";
        }
        catch (Exception exception)
        {
            if (IsCurrentNavigation(navigation))
            {
                SetShellError(OperationError.FromException(exception, "Текстовая практика не загрузилась."));
            }
        }
        finally
        {
            if (IsCurrentNavigation(navigation)) IsShellBusy = false;
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
            case "test":
                _vocabularyTest.Activate();
                break;
            case "path" when !initializedNow:
                await _coursePath.ActivateAsync(_lifetime.Token);
                break;
            case "progress" when !initializedNow && _progress is not null:
                await _progress.RefreshCommand.ExecuteAsync();
                break;
            case "audio" when _audio is not null:
                _audio.Activate();
                break;
        }
    }

    private async Task CancelOutgoingWorkAsync()
    {
        if (ReferenceEquals(CurrentViewModel, _courseLesson)) await _courseLesson.CancelActiveWorkAsync();
        if (_audio is not null && ReferenceEquals(CurrentViewModel, _audio)) _audio.CancelPendingOperations();
    }

    private void ApplySettings(AppSettings settings)
    {
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

        if (_currentKey == "course-lesson")
        {
            if (_lastCourseExamLaunch is not null)
            {
                await OpenCourseExamAsync(_lastCourseExamLaunch);
                return;
            }
            if (_lastCourseLessonLaunch is not null)
            {
                await OpenCourseLessonAsync(_lastCourseLessonLaunch);
                return;
            }
            await NavigateAsync("path", cancellationToken);
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

    private long BeginNavigation() => Interlocked.Increment(ref _navigationGeneration);

    private bool IsCurrentNavigation(long navigation) =>
        Volatile.Read(ref _navigationGeneration) == navigation;

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
        var icon = Application.Current?.TryFindResource(iconResource) as Geometry ?? Geometry.Empty;
        return new NavigationItemViewModel(key, title, icon, new RelayCommand(() => Navigate(key)));
    }

    private void SetSelection(string key)
    {
        foreach (var item in NavigationItems) item.IsSelected = item.Key == key;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _courseLesson.CancelActiveWorkAsync();
        if (_audio is not null) await _audio.DisposeAsync();
        _lifetime.Dispose();
    }
}

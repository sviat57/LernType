using System.Collections.ObjectModel;
using System.IO;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Training;
using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.App.ViewModels;

public sealed record AudioPracticePrompt(
    string Key,
    GermanLevel Level,
    string Title,
    string GermanText,
    string SpeakingInstruction,
    int ResponseSeconds)
{
    public string LevelTitle => Level == GermanLevel.A0 ? "Pre-A1" : Level.ToString();
    public string DisplayTitle => $"{LevelTitle} · {Title}";
}

public sealed class AudioPracticeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAudioPracticeService _audio;
    private readonly IAttemptRepository? _attempts;
    private readonly IClock _clock;
    private readonly TemporaryAudioRecordingStore _recordingStore;
    private readonly bool _ownsRecordingStore;
    private readonly object _recordingCleanupSync = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private Task _recordingCleanupTask = Task.CompletedTask;
    private AudioPracticePrompt _selectedPrompt;
    private AudioInputDevice? _selectedDevice;
    private string _transcript = string.Empty;
    private string _feedback = string.Empty;
    private string _errorMessage = string.Empty;
    private string _timerText = string.Empty;
    private bool _isRecording;
    private bool _hasRecording;
    private bool _isInitialized;
    private string? _recordingPath;
    private DateTimeOffset _attemptStartedAtUtc;

    public AudioPracticeViewModel(
        IAudioPracticeService audio,
        IAttemptRepository? attempts = null,
        IClock? clock = null,
        TemporaryAudioRecordingStore? recordingStore = null)
    {
        _audio = audio;
        _attempts = attempts;
        _clock = clock ?? SystemClock.Instance;
        _recordingStore = recordingStore ?? new TemporaryAudioRecordingStore();
        _ownsRecordingStore = recordingStore is null;
        _attemptStartedAtUtc = _clock.UtcNow;
        Prompts = new(CreatePrompts());
        _selectedPrompt = Prompts[0];
        ListenCommand = new AsyncRelayCommand(ListenAsync, () => HasGermanVoice, HandleCommandError);
        StartTimedRecordingCommand = new AsyncRelayCommand(StartTimedRecordingAsync, () => HasInputDevice && !IsRecording, HandleCommandError);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording, HandleCommandError);
        PlayRecordingCommand = new AsyncRelayCommand(PlayRecordingAsync, () => HasRecording && !IsRecording, HandleCommandError);
        CheckTranscriptCommand = new AsyncRelayCommand(CheckTranscriptAsync, () => !string.IsNullOrWhiteSpace(Transcript), HandleCommandError);
        RateSpeakingCommand = new AsyncParameterizedRelayCommand(RateSpeakingAsync, parameter =>
            HasRecording && parameter is string value && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _),
            HandleCommandError);
    }

    public ObservableCollection<AudioPracticePrompt> Prompts { get; }
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = [];
    public AsyncRelayCommand ListenCommand { get; }
    public AsyncRelayCommand StartTimedRecordingCommand { get; }
    public AsyncRelayCommand StopRecordingCommand { get; }
    public AsyncRelayCommand PlayRecordingCommand { get; }
    public AsyncRelayCommand CheckTranscriptCommand { get; }
    public AsyncParameterizedRelayCommand RateSpeakingCommand { get; }

    public AudioPracticePrompt SelectedPrompt
    {
        get => _selectedPrompt;
        set
        {
            if (SetProperty(ref _selectedPrompt, value))
            {
                ResetAttempt();
            }
        }
    }

    public AudioInputDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => SetProperty(ref _selectedDevice, value);
    }

    public string Transcript
    {
        get => _transcript;
        set
        {
            if (SetProperty(ref _transcript, value))
            {
                CheckTranscriptCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string TimerText { get => _timerText; private set => SetProperty(ref _timerText, value); }
    public bool HasGermanVoice { get; private set; }
    public bool HasInputDevice => InputDevices.Count > 0;
    public bool CanSelectAudioOptions => !IsRecording;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                StartTimedRecordingCommand.RaiseCanExecuteChanged();
                StopRecordingCommand.RaiseCanExecuteChanged();
                PlayRecordingCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSelectAudioOptions));
            }
        }
    }
    public bool HasRecording
    {
        get => _hasRecording;
        private set
        {
            if (SetProperty(ref _hasRecording, value))
            {
                PlayRecordingCommand.RaiseCanExecuteChanged();
                RateSpeakingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await _recordingStore.CleanupOrphansAsync();
        try
        {
            HasGermanVoice = _audio.GetSpeechVoices().Any(voice =>
                voice.IsEnabled && voice.CultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase));
            foreach (var device in _audio.GetInputDevices())
            {
                InputDevices.Add(device);
            }
            SelectedDevice = InputDevices.FirstOrDefault();
            if (!HasGermanVoice)
            {
                ErrorMessage = "Для аудирования установите немецкий голос в настройках речи Windows.";
            }
            else if (!HasInputDevice)
            {
                ErrorMessage = "Микрофон не найден. Аудирование остаётся доступным.";
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = OperationError.FromException(exception, "Не удалось проверить аудиоустройства.").UserMessage;
        }
        OnPropertyChanged(nameof(HasGermanVoice));
        OnPropertyChanged(nameof(HasInputDevice));
        OnPropertyChanged(nameof(HasError));
        ListenCommand.RaiseCanExecuteChanged();
        StartTimedRecordingCommand.RaiseCanExecuteChanged();
    }

    public void Activate()
    {
        _attemptStartedAtUtc = _clock.UtcNow;
        ErrorMessage = !HasGermanVoice
            ? "Для аудирования установите немецкий голос в настройках речи Windows."
            : !HasInputDevice
                ? "Микрофон не найден. Аудирование остаётся доступным."
                : string.Empty;
        OnPropertyChanged(nameof(HasError));
    }

    public void CancelPendingOperations()
    {
        ListenCommand.Cancel();
        StartTimedRecordingCommand.Cancel();
        StopRecordingCommand.Cancel();
        PlayRecordingCommand.Cancel();
        _audio.StopPlayback();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        ClearError();
        await _audio.SpeakAsync(SelectedPrompt.GermanText, "de-DE", GetSpeechRate(SelectedPrompt.Level), cancellationToken);
    }

    private async Task StartTimedRecordingAsync(CancellationToken cancellationToken)
    {
        ClearError();
        HasRecording = false;
        DeleteRecording();
        for (var second = 3; second > 0; second--)
        {
            TimerText = $"Подготовка: {second}";
            await _clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        _recordingPath = _recordingStore.CreateRecordingPath();
        await _audio.StartRecordingAsync(_recordingPath, SelectedDevice?.DeviceNumber ?? 0, cancellationToken);
        IsRecording = true;
        try
        {
            for (var second = SelectedPrompt.ResponseSeconds; second > 0 && IsRecording; second--)
            {
                TimerText = $"Ответ: {second} сек.";
                await _clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            if (IsRecording)
            {
                await StopRecordingCoreAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsRecording)
            {
                await StopRecordingCoreAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        StartTimedRecordingCommand.Cancel();
        return IsRecording ? StopRecordingCoreAsync(cancellationToken) : Task.CompletedTask;
    }

    private async Task StopRecordingCoreAsync(CancellationToken cancellationToken)
    {
        _recordingPath = await _audio.StopRecordingAsync(cancellationToken);
        IsRecording = false;
        HasRecording = File.Exists(_recordingPath);
        TimerText = HasRecording ? "Запись готова к прослушиванию" : string.Empty;
    }

    private async Task PlayRecordingAsync(CancellationToken cancellationToken)
    {
        if (_recordingPath is null)
        {
            return;
        }
        ClearError();
        await _audio.PlayAsync(_recordingPath, cancellationToken);
    }

    private async Task CheckTranscriptAsync(CancellationToken cancellationToken)
    {
        var evaluation = AnswerEvaluator.Evaluate(Transcript, SelectedPrompt.GermanText, "de-DE");
        Feedback = evaluation.IsCorrect
            ? "Текст распознан точно."
            : $"Сравните с эталоном: {SelectedPrompt.GermanText}";
        OnPropertyChanged(nameof(HasFeedback));
        await RecordAttemptAsync(LanguageSkill.Listening, ExerciseType.Dictation,
            AttemptDirection.GermanComprehension, evaluation.IsCorrect ? 1 : 0,
            EvidenceQuality.Deterministic, cancellationToken);
    }

    private async Task RateSpeakingAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not string text ||
            !double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var score))
        {
            return;
        }
        score = Math.Clamp(score, 0, 1);
        Feedback = score switch
        {
            >= 0.9 => "Ответ прозвучал уверенно. Запланировано более редкое повторение.",
            >= 0.65 => "Смысл передан. Повторите ещё раз, работая над плавностью.",
            _ => "Разберите образец и повторите ответ короткими смысловыми группами."
        };
        OnPropertyChanged(nameof(HasFeedback));
        await RecordAttemptAsync(LanguageSkill.Speaking, ExerciseType.SpokenResponse,
            AttemptDirection.GermanProduction, score, EvidenceQuality.SelfReported, cancellationToken);
    }

    private Task RecordAttemptAsync(
        LanguageSkill skill,
        ExerciseType family,
        AttemptDirection direction,
        double score,
        EvidenceQuality quality,
        CancellationToken cancellationToken)
    {
        if (_attempts is null)
        {
            return Task.CompletedTask;
        }
        var now = _clock.UtcNow;
        var objective = GermanCurriculum.CreateDefault().Levels
            .Single(level => level.Level == SelectedPrompt.Level).Objectives
            .First(item => item.Skill == skill);
        var attempt = new AttemptEvent(
            Guid.NewGuid(),
            $"audio.{SelectedPrompt.Level.ToString().ToLowerInvariant()}.{SelectedPrompt.Key}.{skill.ToString().ToLowerInvariant()}",
            1,
            SelectedPrompt.Level,
            skill,
            family,
            direction,
            score,
            AssessmentMode.Practice,
            _attemptStartedAtUtc,
            now,
            _sessionId,
            skill == LanguageSkill.Speaking ? "self-review-v1" : "dictation-v1",
            quality,
            objective.Id);
        _attemptStartedAtUtc = now;
        return _attempts.AppendAsync(attempt, cancellationToken);
    }

    private void ResetAttempt()
    {
        Transcript = string.Empty;
        Feedback = string.Empty;
        TimerText = string.Empty;
        HasRecording = false;
        _attemptStartedAtUtc = _clock.UtcNow;
        DeleteRecording();
        OnPropertyChanged(nameof(HasFeedback));
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(HasError));
    }

    private void HandleCommandError(OperationError error)
    {
        ErrorMessage = error.UserMessage;
        OnPropertyChanged(nameof(HasError));
    }

    private void DeleteRecording()
    {
        var path = _recordingPath;
        _recordingPath = null;
        if (path is null)
        {
            return;
        }

        _audio.StopPlayback();
        lock (_recordingCleanupSync)
        {
            _recordingCleanupTask = DeleteRecordingAfterAsync(_recordingCleanupTask, path);
        }
    }

    private async Task DeleteRecordingAfterAsync(Task previousCleanup, string path)
    {
        try
        {
            await previousCleanup.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the queue moving; the session directory is still owned by the store janitor.
        }
        await _recordingStore.DeleteAsync(path).ConfigureAwait(false);
    }

    private Task GetPendingRecordingCleanup()
    {
        lock (_recordingCleanupSync)
        {
            return _recordingCleanupTask;
        }
    }

    private static int GetSpeechRate(GermanLevel level) => level switch
    {
        GermanLevel.A0 => -3,
        GermanLevel.A1 => -2,
        GermanLevel.A2 => -1,
        _ => 0
    };

    private static IEnumerable<AudioPracticePrompt> CreatePrompts() =>
    [
        new("greeting", GermanLevel.A0, "Приветствие", "Guten Morgen. Ich heiße Lena.", "Поздоровайтесь и назовите своё имя.", 25),
        new("numbers", GermanLevel.A0, "Числа", "Meine Telefonnummer ist null drei null, sieben fünf, zwei eins.", "Назовите номер телефона группами цифр.", 25),
        new("origin", GermanLevel.A0, "О себе", "Ich komme aus Russland und lerne Deutsch.", "Скажите, откуда вы и какой язык изучаете.", 25),
        new("appointment", GermanLevel.A1, "Встреча", "Treffen wir uns am Freitag um halb sechs vor dem Bahnhof?", "Предложите время и место встречи.", 35),
        new("shopping", GermanLevel.A1, "Покупка", "Entschuldigung, wie viel kostet dieses Brot?", "Вежливо спросите цену товара.", 35),
        new("invitation", GermanLevel.A1, "Приглашение", "Am Samstag feiere ich meinen Geburtstag. Kommst du auch?", "Пригласите друга и задайте вопрос.", 35),
        new("travel", GermanLevel.A2, "Поездка", "Wegen der Verspätung komme ich erst gegen neun Uhr an.", "Объясните задержку и новое время прибытия.", 45),
        new("health", GermanLevel.A2, "Здоровье", "Seit gestern habe ich Halsschmerzen und kann kaum schlafen.", "Опишите врачу симптомы и их длительность.", 45),
        new("plan", GermanLevel.A2, "Совместный план", "Wir könnten zuerst einkaufen und danach gemeinsam kochen.", "Предложите два последовательных действия.", 45),
        new("experience", GermanLevel.B1, "Опыт", "Diese Erfahrung hat mir gezeigt, wie wichtig gute Vorbereitung ist.", "Расскажите об опыте и сформулируйте вывод.", 60),
        new("opinion", GermanLevel.B1, "Мнение", "Meiner Ansicht nach überwiegen die Vorteile, obwohl es auch Risiken gibt.", "Выскажите мнение, преимущество и оговорку.", 60),
        new("presentation", GermanLevel.B1, "Мини-презентация", "Heute möchte ich über nachhaltige Mobilität in meiner Stadt sprechen.", "Откройте короткую презентацию и обозначьте тему.", 60),
        new("debate", GermanLevel.B2, "Дискуссия", "Dem Argument stimme ich teilweise zu; entscheidend ist jedoch die konkrete Umsetzung.", "Частично согласитесь и добавьте контраргумент.", 75),
        new("proposal", GermanLevel.B2, "Предложение", "Eine praktikable Lösung wäre, das Projekt zunächst in kleinem Umfang zu testen.", "Предложите реалистичное решение и первый шаг.", 75),
        new("comparison", GermanLevel.B2, "Сопоставление", "Während das erste Modell kurzfristig günstiger ist, bietet das zweite langfristig mehr Sicherheit.", "Сопоставьте два варианта по разным критериям.", 75),
        new("lecture", GermanLevel.C1, "Доклад", "Die vorliegenden Daten legen nahe, dass strukturelle Faktoren stärker wirken als individuelle Entscheidungen.", "Сформулируйте вывод из условных данных и ограничение.", 90),
        new("qualification", GermanLevel.C1, "Точная оговорка", "Diese Schlussfolgerung gilt allerdings nur unter den zuvor genannten Voraussetzungen.", "Ограничьте область применимости своего вывода.", 90),
        new("synthesis", GermanLevel.C1, "Синтез", "Beide Positionen beruhen auf plausiblen Annahmen, setzen jedoch unterschiedliche Prioritäten.", "Объедините две позиции и назовите различие.", 90),
        new("nuance", GermanLevel.C2, "Смысловой нюанс", "Der Vorschlag ist weniger unrealistisch als vielmehr politisch schwer vermittelbar.", "Точно переосмыслите первоначальную оценку предложения.", 120),
        new("reframe", GermanLevel.C2, "Переформулирование", "Was zunächst wie ein Widerspruch erscheint, erweist sich bei näherer Betrachtung als Perspektivwechsel.", "Переформулируйте кажущееся противоречие.", 120),
        new("register", GermanLevel.C2, "Регистр", "Ich möchte diese Einschätzung mit allem gebotenen Respekt entschieden zurückweisen.", "Вежливо, но категорично отвергните позицию.", 120)
    ];

    public async ValueTask DisposeAsync()
    {
        ListenCommand.Cancel();
        StartTimedRecordingCommand.Cancel();
        StopRecordingCommand.Cancel();
        PlayRecordingCommand.Cancel();
        try
        {
            if (IsRecording)
            {
                try
                {
                    _recordingPath = await _audio.StopRecordingAsync();
                    IsRecording = false;
                }
                catch (InvalidOperationException)
                {
                    // Recording was already released by the device callback.
                }
            }
        }
        finally
        {
            _audio.StopPlayback();
            DeleteRecording();
            try
            {
                await _audio.DisposeAsync();
            }
            finally
            {
                try
                {
                    await GetPendingRecordingCleanup();
                }
                finally
                {
                    if (_ownsRecordingStore)
                    {
                        await _recordingStore.DisposeAsync();
                    }
                }
            }
        }
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Training;
using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.App.ViewModels;

/// <summary>Runs one structured course lesson or one course-local final examination.</summary>
public sealed class CourseLessonViewModel : ObservableObject
{
    private readonly ICourseCatalogRepository _catalogRepository;
    private readonly ICourseProgressRepository _progressRepository;
    private readonly IAttemptRepository _attemptRepository;
    private readonly IAudioPracticeService? _audio;
    private readonly TemporaryAudioRecordingStore? _recordingStore;
    private readonly Action _returnToCourses;
    private CourseCatalog? _catalog;
    private CourseDefinition? _course;
    private CourseUnitDefinition? _unit;
    private CourseLessonDefinition? _lesson;
    private CourseExamDefinition? _exam;
    private int _position;
    private string _answerText = string.Empty;
    private string? _selectedOption;
    private string _feedback = string.Empty;
    private string _audioStatus = string.Empty;
    private string _resultText = string.Empty;
    private bool _isTaskAnswered;
    private bool _isFlowComplete;
    private bool _isBusy;
    private bool _isRecording;
    private bool _hasRecording;
    private bool _recordingUnavailable;
    private bool _audioInitialized;
    private bool _hasGermanVoice;
    private AudioInputDevice? _inputDevice;
    private string? _recordingPath;
    private Guid _sessionId = Guid.NewGuid();
    private DateTimeOffset _taskStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, double> _scores = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selfReportedTaskKeys = new(StringComparer.Ordinal);

    public CourseLessonViewModel(
        ICourseCatalogRepository catalogRepository,
        ICourseProgressRepository progressRepository,
        IAttemptRepository attemptRepository,
        Action returnToCourses,
        IAudioPracticeService? audio = null,
        TemporaryAudioRecordingStore? recordingStore = null)
    {
        _catalogRepository = catalogRepository ?? throw new ArgumentNullException(nameof(catalogRepository));
        _progressRepository = progressRepository ?? throw new ArgumentNullException(nameof(progressRepository));
        _attemptRepository = attemptRepository ?? throw new ArgumentNullException(nameof(attemptRepository));
        _returnToCourses = returnToCourses ?? throw new ArgumentNullException(nameof(returnToCourses));
        _audio = audio;
        _recordingStore = recordingStore;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit, SetError);
        NextCommand = new AsyncRelayCommand(NextAsync, CanMoveNext, SetError);
        ListenCommand = new AsyncRelayCommand(ListenAsync, () => HasGermanVoice && !IsBusy, SetError);
        StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync, () => IsSpeakingTask && HasMicrophone && !IsRecording && !IsBusy && !IsTaskAnswered, SetError);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsSpeakingTask && IsRecording, SetError);
        PlayRecordingCommand = new AsyncRelayCommand(PlayRecordingAsync, () => IsSpeakingTask && HasRecording && !IsRecording && !IsBusy, SetError);
        CompleteSpeakingCommand = new AsyncRelayCommand(
            CompleteSpeakingAsync,
            () => IsSpeakingTask && !IsRecording && (HasRecording || !HasMicrophone || _recordingUnavailable) && !IsTaskAnswered,
            SetError);
        ReturnCommand = new RelayCommand(_returnToCourses);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => _course is not null && (_lesson is not null || _exam is not null), SetError);
    }

    public ObservableCollection<string> Options { get; } = [];
    public AsyncRelayCommand SubmitCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand ListenCommand { get; }
    public AsyncRelayCommand StartRecordingCommand { get; }
    public AsyncRelayCommand StopRecordingCommand { get; }
    public AsyncRelayCommand PlayRecordingCommand { get; }
    public AsyncRelayCommand CompleteSpeakingCommand { get; }
    public RelayCommand ReturnCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }

    public bool IsExamMode => _exam is not null;
    public string FlowLabel => IsExamMode ? "ВНУТРЕННИЙ ЭКЗАМЕН" : $"УРОК {LessonSequence:00}";
    public string CourseTitle => _course?.Title ?? string.Empty;
    public string FlowTitle => IsExamMode ? _exam?.Title ?? string.Empty : _lesson?.Title ?? string.Empty;
    public string FlowOutcome => IsExamMode
        ? "Ответы не раскрываются до завершения. Устная часть сохраняется как самопроверка."
        : _lesson?.Outcome ?? string.Empty;
    public int LessonSequence => _course is null || _lesson is null ? 0 :
        _course.Units.OrderBy(item => item.Order)
            .SelectMany(item => item.Lessons.OrderBy(lesson => lesson.Order))
            .TakeWhile(lesson => lesson.Id != _lesson.Id).Count() + 1;
    public int TotalPositions => IsExamMode ? _exam?.Questions.Count ?? 0 : _lesson?.Steps.Count ?? 0;
    public string PositionText => $"{Math.Min(_position + 1, Math.Max(TotalPositions, 1))} из {TotalPositions}";
    public double ProgressValue => TotalPositions == 0 ? 0 : 100d * Math.Min(_position + (IsTaskAnswered ? 1 : 0), TotalPositions) / TotalPositions;
    public string StepKindText => IsExamMode ? CurrentExamQuestion?.Skill switch
    {
        LanguageSkill.Reading => "Чтение",
        LanguageSkill.Writing => "Письмо",
        LanguageSkill.Listening => "Аудирование",
        LanguageSkill.Speaking => "Говорение",
        LanguageSkill.Grammar => "Грамматика",
        _ => "Проверка"
    } : CurrentStep?.Kind switch
    {
        CourseStepKind.Briefing => "Коротко о главном",
        CourseStepKind.Writing => "Письмо",
        CourseStepKind.Reading => "Чтение",
        CourseStepKind.ListeningSpeaking => "Слушаем и говорим",
        CourseStepKind.Rule => "Второе правило",
        CourseStepKind.Checkpoint => "Контроль шага",
        _ => string.Empty
    };
    public string StepTitle => IsExamMode ? CurrentExamQuestion?.Title ?? string.Empty : CurrentStep?.Title ?? string.Empty;
    public string Instruction => IsExamMode ? CurrentExamQuestion?.Prompt ?? string.Empty : CurrentStep?.Instruction ?? string.Empty;
    public string TaskPrompt => IsExamMode ? string.Empty : CurrentTask?.Prompt ?? string.Empty;
    public bool HasTaskPrompt => !string.IsNullOrWhiteSpace(TaskPrompt);
    public string RussianText => IsExamMode ? string.Empty : CurrentStep?.RussianText ?? string.Empty;
    public string GermanText => IsExamMode ? string.Empty : CurrentStep?.GermanText ?? string.Empty;
    public string Hint => IsExamMode ? string.Empty : CurrentStep?.Hint ?? string.Empty;
    public bool HasRussianText => !string.IsNullOrWhiteSpace(RussianText);
    public bool HasGermanText => !string.IsNullOrWhiteSpace(GermanText);
    public bool HasHint => !string.IsNullOrWhiteSpace(Hint) && (!IsExamMode || IsFlowComplete);
    public string TableText => FormatTable(CurrentStep?.Table);
    public bool HasTable => !string.IsNullOrWhiteSpace(TableText);
    public CourseTaskDefinition? CurrentTask => IsExamMode || CurrentStep?.Task is null ? null : CurrentStep.Task;
    public CourseExamQuestionDefinition? CurrentExamQuestion =>
        _exam is not null && _position >= 0 && _position < _exam.Questions.Count ? _exam.Questions[_position] : null;
    public CourseStepDefinition? CurrentStep =>
        _lesson is not null && _position >= 0 && _position < _lesson.Steps.Count ? _lesson.Steps[_position] : null;
    public CourseTaskKind? CurrentTaskKind => IsExamMode ? CurrentExamQuestion?.Kind : CurrentTask?.Kind;
    public bool HasTask => CurrentTaskKind is not null;
    public bool ShowAnswerInput => CurrentTaskKind is CourseTaskKind.ShortAnswer or CourseTaskKind.GapFill;
    public bool ShowOptions => CurrentTaskKind == CourseTaskKind.SingleChoice;
    public bool IsSpeakingTask => CurrentTaskKind == CourseTaskKind.SelfRecordedSpeech;
    public bool IsListeningTask => IsExamMode && CurrentExamQuestion?.Skill == LanguageSkill.Listening;
    public bool ShowAudioTask => IsSpeakingTask || IsListeningTask;
    public string AudioSectionTitle => IsListeningTask ? "АУДИРОВАНИЕ" : "УСТНЫЙ ОТВЕТ";
    public string AudioInstructionText => IsListeningTask
        ? "Прослушайте немецкий фрагмент без подсказки, затем ответьте на вопрос. Фрагмент можно включить повторно."
        : "Сначала прослушайте модель, затем запишите себя и сравните две версии. Произношение оцениваете вы — приложение не выдаёт автоматическую оценку речи.";
    public string ListenButtonText => IsListeningTask ? "Прослушать фрагмент" : "Слушать модель";
    public bool ShowListeningTranscriptFallback => IsListeningTask && !HasGermanVoice;
    public string ListeningTranscriptFallbackText => ShowListeningTranscriptFallback
        ? $"Немецкий голос Windows не установлен. Режим чтения вместо аудио: {CurrentExamQuestion?.AudioText}"
        : string.Empty;
    public string SpeakingModelText => IsSpeakingTask
        ? (IsExamMode ? CurrentExamQuestion?.ModelAnswer : CurrentTask?.ModelAnswer) ?? string.Empty
        : string.Empty;
    public bool ShowSpeakingModelText => IsSpeakingTask
        && !string.IsNullOrWhiteSpace(SpeakingModelText)
        && (!HasGermanVoice || IsTaskAnswered);
    public bool ShowSubmit => HasTask && !IsSpeakingTask && !IsTaskAnswered;
    public bool CanEditAnswer => !IsTaskAnswered && !IsBusy;
    public bool ShowPassiveContinue => !HasTask;
    public bool IsTaskAnswered
    {
        get => _isTaskAnswered;
        private set
        {
            if (!SetProperty(ref _isTaskAnswered, value)) return;
            OnPropertyChanged(nameof(ShowSubmit));
            OnPropertyChanged(nameof(CanEditAnswer));
            OnPropertyChanged(nameof(ShowSpeakingModelText));
            RaiseCommandStates();
        }
    }
    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
    public string Feedback { get => _feedback; private set { if (SetProperty(ref _feedback, value)) OnPropertyChanged(nameof(HasFeedback)); } }
    public string AnswerText
    {
        get => _answerText;
        set
        {
            if (SetProperty(ref _answerText, value)) SubmitCommand.RaiseCanExecuteChanged();
        }
    }
    public string? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value)) SubmitCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsFlowComplete { get => _isFlowComplete; private set { if (SetProperty(ref _isFlowComplete, value)) RaiseAll(); } }
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);
    public string ResultText { get => _resultText; private set { if (SetProperty(ref _resultText, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEditAnswer));
            RaiseCommandStates();
        }
    }
    public bool HasGermanVoice => _hasGermanVoice;
    public bool HasMicrophone => _inputDevice is not null;
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value)) RaiseCommandStates();
        }
    }
    public bool HasRecording
    {
        get => _hasRecording;
        private set
        {
            if (SetProperty(ref _hasRecording, value)) RaiseCommandStates();
        }
    }
    public bool HasAudioStatus => !string.IsNullOrWhiteSpace(AudioStatus);
    public string AudioStatus { get => _audioStatus; private set { if (SetProperty(ref _audioStatus, value)) OnPropertyChanged(nameof(HasAudioStatus)); } }
    public string SpeakingCompletionText => HasRecording
        ? "Запись готова — засчитать устный шаг"
        : HasMicrophone && !_recordingUnavailable
            ? "Сначала запишите устный ответ"
            : "Отметить устную самопроверку без записи";
    public string NextButtonText => _position + 1 >= TotalPositions ? (IsExamMode ? "Завершить экзамен" : "Завершить урок") : "Далее";

    public async Task PrepareAsync(CourseLessonLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await ResetAudioAsync();
        _catalog ??= await _catalogRepository.LoadAsync(cancellationToken);
        _course = _catalog.Track.Courses.Single(course => course.Id == launch.CourseId && course.Availability == CourseAvailability.Published);
        _unit = _course.Units.Single(unit => unit.Id == launch.UnitId);
        _lesson = _unit.Lessons.Single(lesson => lesson.Id == launch.LessonId);
        _exam = null;
        _position = launch.StepId is null ? 0 : Math.Max(0, _lesson.Steps.ToList().FindIndex(step => step.Id == launch.StepId));
        var resume = launch.StepId is null ? null : await _progressRepository.GetResumeAsync(_course.Id, cancellationToken);
        var matchingResume = resume is not null
            && resume.UnitId == _unit.Id
            && resume.LessonId == _lesson.Id
            && resume.StepId == launch.StepId
            ? resume
            : null;
        await BeginFlowAsync(cancellationToken, matchingResume);
    }

    public async Task PrepareExamAsync(CourseExamLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await ResetAudioAsync();
        _catalog ??= await _catalogRepository.LoadAsync(cancellationToken);
        _course = _catalog.Track.Courses.Single(course => course.Id == launch.CourseId && course.Availability == CourseAvailability.Published);
        _exam = _course.Exam is { } exam && exam.Id == launch.ExamId ? exam : throw new InvalidOperationException("Экзамен не найден в выбранном курсе.");
        _unit = null;
        _lesson = null;
        _position = 0;
        await BeginFlowAsync(cancellationToken);
    }

    public async Task CancelActiveWorkAsync()
    {
        var commands = new[] { ListenCommand, StartRecordingCommand, StopRecordingCommand, PlayRecordingCommand };
        foreach (var command in commands) command.Cancel();
        var activeTasks = commands
            .Select(command => command.ExecutionTask)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (activeTasks.Length > 0) await Task.WhenAll(activeTasks);
        _audio?.StopPlayback();
        await ResetAudioAsync();
    }

    private async Task BeginFlowAsync(
        CancellationToken cancellationToken,
        CourseResumeState? resume = null)
    {
        _sessionId = Guid.NewGuid();
        _scores.Clear();
        _selfReportedTaskKeys.Clear();
        RestoreResumeSnapshot(resume);
        IsFlowComplete = false;
        ResultText = string.Empty;
        await EnsureAudioInitializedAsync(cancellationToken);
        ResetCurrentPosition();
        await SaveResumeAsync(cancellationToken);
        RaiseAll();
    }

    private void RestoreResumeSnapshot(CourseResumeState? resume)
    {
        if (resume is null || _lesson is null) return;
        var validTasks = _lesson.Steps
            .Where(step => step.Task is not null)
            .ToDictionary(
                step => $"{step.Id}:{step.Task!.Id}",
                step => step.Task!,
                StringComparer.Ordinal);
        foreach (var (taskKey, score) in resume.TaskScores)
        {
            if (validTasks.ContainsKey(taskKey)) _scores[taskKey] = score;
        }
        foreach (var taskKey in resume.SelfReportedTaskKeys)
        {
            if (validTasks.TryGetValue(taskKey, out var task)
                && task.Kind == CourseTaskKind.SelfRecordedSpeech
                && _scores.ContainsKey(taskKey))
            {
                _selfReportedTaskKeys.Add(taskKey);
            }
        }
    }

    private async Task EnsureAudioInitializedAsync(CancellationToken cancellationToken)
    {
        if (_audioInitialized || _audio is null) return;
        _audioInitialized = true;
        if (_recordingStore is not null) await _recordingStore.CleanupOrphansAsync(cancellationToken);
        try
        {
            _hasGermanVoice = _audio.GetSpeechVoices().Any(voice => voice.IsEnabled && voice.CultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase));
            _inputDevice = _audio.GetInputDevices().FirstOrDefault();
            AudioStatus = !_hasGermanVoice
                ? "Немецкий голос Windows не установлен. Текст модели остаётся на экране."
                : _inputDevice is null
                    ? "Микрофон не найден. Устный шаг можно отметить как самопроверку."
                    : string.Empty;
        }
        catch (Exception exception)
        {
            AudioStatus = OperationError.FromException(exception, "Аудиоустройства не готовы; урок можно продолжить без записи.").UserMessage;
        }
        OnPropertyChanged(nameof(HasGermanVoice));
        OnPropertyChanged(nameof(HasMicrophone));
        OnPropertyChanged(nameof(SpeakingCompletionText));
        OnPropertyChanged(nameof(ShowSpeakingModelText));
    }

    private bool CanSubmit() => HasTask && !IsSpeakingTask && !IsTaskAnswered && !IsBusy &&
        (ShowOptions ? !string.IsNullOrWhiteSpace(SelectedOption) : !string.IsNullOrWhiteSpace(AnswerText));

    private bool CanMoveNext() => !IsFlowComplete && !IsBusy && (!HasTask || IsTaskAnswered);

    private async Task SubmitAsync(CancellationToken cancellationToken)
    {
        var actual = ShowOptions ? SelectedOption ?? string.Empty : AnswerText;
        var expected = IsExamMode ? CurrentExamQuestion?.Answer : CurrentTask?.Answer;
        var accepted = IsExamMode ? CurrentExamQuestion?.AcceptedAnswers : CurrentTask?.AcceptedAnswers;
        if (string.IsNullOrWhiteSpace(expected)) throw new InvalidDataException("Для проверяемого шага отсутствует эталон.");
        var culture = ContainsCyrillic(expected) ? "ru-RU" : "de-DE";
        var evaluation = AnswerEvaluator.Evaluate(actual, expected, accepted ?? [], culture, AnswerEvaluationMode.Strict);
        var score = evaluation.IsCorrect ? 1d : 0d;
        _scores[CurrentTaskKey()] = score;
        await RecordAttemptAsync(score, EvidenceQuality.Deterministic, cancellationToken);
        await SaveStepProgressAsync(score, cancellationToken);
        Feedback = IsExamMode
            ? "Ответ сохранён. Результаты появятся после завершения экзамена."
            : evaluation.IsCorrect
                ? "Верно. Переходите к следующему шагу."
                : $"Пока нет. Нормативный ответ: {expected}. {Hint}".Trim();
        IsTaskAnswered = true;
        OnPropertyChanged(nameof(ProgressValue));
    }

    private async Task CompleteSpeakingAsync(CancellationToken cancellationToken)
    {
        var taskKey = CurrentTaskKey();
        _scores[taskKey] = 1;
        _selfReportedTaskKeys.Add(taskKey);
        await RecordAttemptAsync(1, EvidenceQuality.SelfReported, cancellationToken);
        await SaveStepProgressAsync(1, cancellationToken);
        Feedback = IsExamMode
            ? "Устная часть отмечена как выполненная самопроверка."
            : HasRecording
                ? "Устный шаг выполнен. Сравните запись с моделью и продолжайте."
                : "Устный шаг отмечен как самопроверка без записи.";
        IsTaskAnswered = true;
        OnPropertyChanged(nameof(ProgressValue));
    }

    private async Task NextAsync(CancellationToken cancellationToken)
    {
        if (_position + 1 >= TotalPositions)
        {
            await CompleteFlowAsync(cancellationToken);
            return;
        }
        await ResetAudioAsync();
        _position++;
        ResetCurrentPosition();
        await SaveResumeAsync(cancellationToken);
        RaiseAll();
    }

    private async Task CompleteFlowAsync(CancellationToken cancellationToken)
    {
        // Speaking remains mandatory evidence, but a self-rating must not inflate the
        // deterministic lesson or exam score.
        var graded = _scores
            .Where(item => !_selfReportedTaskKeys.Contains(item.Key))
            .Select(item => item.Value)
            .ToArray();
        var score = graded.Length == 0 ? 0 : graded.Average();
        if (IsExamMode)
        {
            var passed = score * 100 >= _exam!.PassPercent;
            var previous = (await _progressRepository.GetCourseAsync(_course!.Id, cancellationToken))
                .FirstOrDefault(item => item.NodeId == CoursePathViewModel.ExamNodeId(_exam.Id));
            var durableStatus = passed || previous?.Status == CourseNodeStatus.Passed
                ? CourseNodeStatus.Passed
                : CourseNodeStatus.Completed;
            await _progressRepository.UpsertAsync(new(
                _course.Id,
                CoursePathViewModel.ExamNodeId(_exam.Id),
                durableStatus,
                Math.Max(previous?.BestScore ?? 0, score),
                (previous?.AttemptCount ?? 0) + 1,
                DateTimeOffset.UtcNow), cancellationToken);
            ResultText = passed
                ? $"Экзамен пройден: {score:P0}. Это внутренняя итоговая проверка LernType."
                : previous?.Status == CourseNodeStatus.Passed
                    ? $"Текущая попытка: {score:P0}. Экзамен уже пройден ранее; сохранён лучший результат {Math.Max(previous.BestScore, score):P0}."
                    : $"Экзамен завершён: {score:P0}. Для прохождения нужно {_exam.PassPercent}%. Повторите слабые уроки и попробуйте ещё раз.";
        }
        else
        {
            var completed = score >= 0.60;
            var nodeId = CoursePathViewModel.LessonNodeId(_lesson!.Id);
            var previous = (await _progressRepository.GetCourseAsync(_course!.Id, cancellationToken))
                .FirstOrDefault(item => item.NodeId == nodeId);
            var durableStatus = completed || previous?.Status >= CourseNodeStatus.Completed
                ? CourseNodeStatus.Completed
                : CourseNodeStatus.InProgress;
            await _progressRepository.UpsertAsync(new(
                _course.Id,
                nodeId,
                durableStatus,
                Math.Max(previous?.BestScore ?? 0, score),
                (previous?.AttemptCount ?? 0) + 1,
                DateTimeOffset.UtcNow), cancellationToken);
            ResultText = completed
                ? $"Урок пройден: {score:P0}. Следующий урок открыт."
                : previous?.Status >= CourseNodeStatus.Completed
                    ? $"Текущая попытка: {score:P0}. Урок уже пройден ранее, поэтому следующий урок остаётся открыт; лучший результат {Math.Max(previous.BestScore, score):P0}."
                    : $"Урок выполнен на {score:P0}. Для открытия следующего урока нужно 60%; повторите этот урок.";
        }
        IsFlowComplete = true;
        Feedback = string.Empty;
        await ResetCompletedLessonResumeAsync(cancellationToken);
        await ResetAudioAsync();
    }

    private async Task ResetCompletedLessonResumeAsync(CancellationToken cancellationToken)
    {
        if (_course is null || _unit is null || _lesson is null) return;
        var firstStep = _lesson.Steps.OrderBy(step => step.Order).FirstOrDefault();
        if (firstStep is null) return;
        await _progressRepository.SaveResumeAsync(new(
            _course.Id,
            _unit.Id,
            _lesson.Id,
            firstStep.Id,
            DateTimeOffset.UtcNow), cancellationToken);
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await ResetAudioAsync();
        _position = 0;
        await BeginFlowAsync(cancellationToken);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_audio is null || !HasGermanVoice) return;
        var text = AudioModelText();
        if (string.IsNullOrWhiteSpace(text)) return;
        IsBusy = true;
        try
        {
            AudioStatus = "Воспроизводим немецкую модель…";
            await _audio.SpeakAsync(text, "de-DE", -1, cancellationToken);
            AudioStatus = IsListeningTask
                ? "Фрагмент завершён. Теперь ответьте на вопрос."
                : HasMicrophone
                    ? "Теперь запишите свой ответ."
                    : "Повторите модель вслух.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartRecordingAsync(CancellationToken cancellationToken)
    {
        if (_audio is null || _inputDevice is null) return;
        if (_recordingStore is null)
        {
            EnableRecordingFallback("Временное хранилище записи недоступно. Произнесите ответ вслух и отметьте самопроверку без файла.");
            return;
        }
        try
        {
            await ResetAudioAsync();
            _recordingUnavailable = false;
            OnPropertyChanged(nameof(SpeakingCompletionText));
            _recordingPath = _recordingStore.CreateRecordingPath();
            await _audio.StartRecordingAsync(_recordingPath, _inputDevice.DeviceNumber, cancellationToken);
            IsRecording = true;
            AudioStatus = "Запись идёт. Произнесите ответ и нажмите «Остановить».";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbandonFailedRecordingAsync();
            throw;
        }
        catch (Exception exception)
        {
            await AbandonFailedRecordingAsync();
            EnableRecordingFallback(RecordingFallbackMessage(
                exception,
                "Запись сейчас недоступна. Произнесите ответ вслух и отметьте самопроверку без файла."));
        }
    }

    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        if (_audio is null || !IsRecording) return;
        try
        {
            _recordingPath = await _audio.StopRecordingAsync(cancellationToken);
            IsRecording = false;
            HasRecording = true;
            AudioStatus = "Запись готова. Прослушайте её и сравните с моделью.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbandonFailedRecordingAsync();
            throw;
        }
        catch (Exception exception)
        {
            await AbandonFailedRecordingAsync();
            EnableRecordingFallback(RecordingFallbackMessage(
                exception,
                "Запись не завершилась корректно. Произнесите ответ вслух и отметьте самопроверку без файла."));
        }
    }

    private async Task PlayRecordingAsync(CancellationToken cancellationToken)
    {
        if (_audio is null || string.IsNullOrWhiteSpace(_recordingPath)) return;
        IsBusy = true;
        try
        {
            await _audio.PlayAsync(_recordingPath, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetAudioAsync()
    {
        _audio?.StopPlayback();
        if (IsRecording && _audio is not null)
        {
            try { _recordingPath = await _audio.StopRecordingAsync(); }
            catch (Exception) { }
        }
        IsRecording = false;
        HasRecording = false;
        if (!string.IsNullOrWhiteSpace(_recordingPath) && _recordingStore is not null)
        {
            try { await _recordingStore.DeleteAsync(_recordingPath); }
            catch (Exception) { }
        }
        _recordingPath = null;
    }

    private async Task AbandonFailedRecordingAsync()
    {
        if (_audio is not null)
        {
            try { _recordingPath = await _audio.StopRecordingAsync(); }
            catch (Exception) { }
        }
        IsRecording = false;
        HasRecording = false;
        if (!string.IsNullOrWhiteSpace(_recordingPath) && _recordingStore is not null)
        {
            try { await _recordingStore.DeleteAsync(_recordingPath); }
            catch (Exception) { }
        }
        _recordingPath = null;
    }

    private void EnableRecordingFallback(string message)
    {
        _recordingUnavailable = true;
        IsRecording = false;
        HasRecording = false;
        AudioStatus = message;
        OnPropertyChanged(nameof(SpeakingCompletionText));
        RaiseCommandStates();
    }

    private static string RecordingFallbackMessage(Exception exception, string guidance)
    {
        var userMessage = OperationError.FromException(exception, guidance).UserMessage;
        return string.Equals(userMessage, guidance, StringComparison.Ordinal)
            ? guidance
            : $"{guidance} {userMessage}";
    }

    private async Task SaveResumeAsync(CancellationToken cancellationToken)
    {
        if (_course is null || _unit is null || _lesson is null || CurrentStep is null) return;
        await _progressRepository.SaveResumeAsync(new(
            _course.Id,
            _unit.Id,
            _lesson.Id,
            CurrentStep.Id,
            DateTimeOffset.UtcNow,
            new Dictionary<string, double>(_scores, StringComparer.Ordinal),
            new HashSet<string>(_selfReportedTaskKeys, StringComparer.Ordinal)), cancellationToken);
    }

    private async Task SaveStepProgressAsync(double score, CancellationToken cancellationToken)
    {
        if (_course is null || _lesson is null || CurrentStep is null) return;
        var nodeId = $"step:{_lesson.Id}:{CurrentStep.Id}";
        var previous = (await _progressRepository.GetCourseAsync(_course.Id, cancellationToken))
            .FirstOrDefault(item => item.NodeId == nodeId);
        await _progressRepository.UpsertAsync(new(
            _course.Id,
            nodeId,
            CourseNodeStatus.Completed,
            Math.Max(previous?.BestScore ?? 0, score),
            (previous?.AttemptCount ?? 0) + 1,
            DateTimeOffset.UtcNow), cancellationToken);
        await SaveResumeAsync(cancellationToken);
    }

    private async Task RecordAttemptAsync(double score, EvidenceQuality quality, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var skill = IsExamMode ? CurrentExamQuestion!.Skill : CurrentTask!.Skill;
        var exercise = IsExamMode ? CurrentExamQuestion!.ExerciseType : CurrentTask!.ExerciseType;
        var eventId = Guid.NewGuid();
        var key = IsExamMode
            ? $"course.{_course!.Id}.exam.{_exam!.Id}.{CurrentExamQuestion!.Id}"
            : $"course.{_course!.Id}.lesson.{_lesson!.Id}.{CurrentStep!.Id}.{CurrentTask!.Id}";
        await _attemptRepository.AppendAsync(new AttemptEvent(
            eventId,
            key,
            _catalog!.Revision,
            _course.Level,
            skill,
            exercise,
            DirectionFor(skill),
            score,
            IsExamMode ? AssessmentMode.MockExam : CurrentStep?.Kind == CourseStepKind.Checkpoint ? AssessmentMode.Checkpoint : AssessmentMode.Practice,
            _taskStartedAtUtc,
            now,
            _sessionId,
            "lerntype-course-original-v1",
            quality,
            $"course.{_course.Id}.{skill.ToString().ToLowerInvariant()}",
            false,
            IsExamMode ? _exam!.Id : null,
            IsExamMode ? _exam!.Id : _lesson!.Id), cancellationToken);
    }

    private void ResetCurrentPosition()
    {
        _recordingUnavailable = false;
        AnswerText = string.Empty;
        SelectedOption = null;
        Feedback = string.Empty;
        IsTaskAnswered = false;
        _taskStartedAtUtc = DateTimeOffset.UtcNow;
        Options.Clear();
        foreach (var option in IsExamMode ? CurrentExamQuestion?.Options ?? [] : CurrentTask?.Options ?? []) Options.Add(option);
        AudioStatus = IsListeningTask && !HasGermanVoice
            ? "Немецкий голос Windows не установлен. Ниже показан текстовый режим этого фрагмента."
            : IsSpeakingTask && !HasGermanVoice
            ? "Немецкий голос Windows не установлен. Прочитайте модель и произнесите ответ самостоятельно."
            : IsSpeakingTask && !HasMicrophone
                ? "Микрофон не найден. Повторите модель вслух и отметьте самопроверку."
                : string.Empty;
    }

    private string CurrentTaskKey() => IsExamMode ? CurrentExamQuestion!.Id : $"{CurrentStep!.Id}:{CurrentTask!.Id}";
    private string AudioModelText()
    {
        var candidates = IsExamMode
            ? CurrentExamQuestion?.Skill == LanguageSkill.Listening
                ? new[] { CurrentExamQuestion.AudioText }
                : new[] { CurrentExamQuestion?.ModelAnswer, CurrentExamQuestion?.Answer }
            : new[] { CurrentTask?.ModelAnswer, CurrentTask?.Answer, CurrentStep?.GermanText };
        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private void SetError(OperationError error)
    {
        Feedback = error.UserMessage;
        AudioStatus = error.UserMessage;
    }

    private void RaiseCommandStates()
    {
        SubmitCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        ListenCommand.RaiseCanExecuteChanged();
        StartRecordingCommand.RaiseCanExecuteChanged();
        StopRecordingCommand.RaiseCanExecuteChanged();
        PlayRecordingCommand.RaiseCanExecuteChanged();
        CompleteSpeakingCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
        {
            nameof(IsExamMode), nameof(FlowLabel), nameof(CourseTitle), nameof(FlowTitle), nameof(FlowOutcome),
            nameof(LessonSequence), nameof(TotalPositions), nameof(PositionText), nameof(ProgressValue), nameof(StepKindText),
            nameof(StepTitle), nameof(Instruction), nameof(TaskPrompt), nameof(HasTaskPrompt), nameof(RussianText), nameof(GermanText), nameof(Hint),
            nameof(HasRussianText), nameof(HasGermanText), nameof(HasHint), nameof(TableText), nameof(HasTable),
            nameof(CurrentTask), nameof(CurrentTaskKind), nameof(HasTask), nameof(ShowAnswerInput), nameof(ShowOptions),
            nameof(IsSpeakingTask), nameof(IsListeningTask), nameof(ShowAudioTask), nameof(AudioSectionTitle),
            nameof(AudioInstructionText), nameof(ListenButtonText), nameof(ShowListeningTranscriptFallback),
            nameof(ListeningTranscriptFallbackText), nameof(SpeakingModelText), nameof(ShowSpeakingModelText),
            nameof(ShowSubmit), nameof(CanEditAnswer), nameof(ShowPassiveContinue),
            nameof(NextButtonText), nameof(SpeakingCompletionText)
        }) OnPropertyChanged(property);
        RaiseCommandStates();
    }

    private static AttemptDirection DirectionFor(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Reading or LanguageSkill.Listening => AttemptDirection.GermanComprehension,
        LanguageSkill.Writing => AttemptDirection.RussianToGerman,
        LanguageSkill.Speaking or LanguageSkill.Grammar => AttemptDirection.GermanProduction,
        _ => AttemptDirection.NotApplicable
    };

    private static bool ContainsCyrillic(string value) => value.Any(character => character is >= '\u0400' and <= '\u04FF');

    private static string FormatTable(CourseTableDefinition? table)
    {
        if (table is null || table.Headers.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine(string.Join("  ·  ", table.Headers));
        builder.AppendLine(new string('─', Math.Min(72, Math.Max(12, table.Headers.Sum(header => header.Length + 3)))));
        foreach (var row in table.Rows) builder.AppendLine(string.Join("  ·  ", row));
        return builder.ToString().TrimEnd();
    }
}

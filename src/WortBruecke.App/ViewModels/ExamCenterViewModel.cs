using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed record ExamLevelOption(string? Level, string Label);
public sealed record ExamTargetOption(GermanLevel Level, string Label);
public sealed record ExamModuleOption(string? ModuleId, string Label);
public sealed record ExamSegmentViewModel(string Title, string Skills, string Duration, string Tasks);
public sealed record ExamReadinessItemViewModel(string Title, string Evidence, string Score, string State);

public sealed class ExamCenterViewModel : ObservableObject
{
    private static readonly HashSet<string> ApprovedHosts =
        ["goethe.de", "telc.net", "testdaf.de", "bamf.de"];

    private readonly IExamBlueprintRepository _examRepository;
    private readonly IAttemptRepository? _attemptRepository;
    private readonly ILearningProgressRepository? _compatibilityRepository;
    private readonly Action<string> _navigate;
    private readonly List<ExamBlueprint> _allExams = [];
    private IReadOnlyList<AttemptEvent> _attempts = [];
    private ExamLevelOption? _selectedLevel;
    private ExamBlueprint? _selectedExam;
    private ExamTargetOption? _selectedTarget;
    private ExamModuleOption? _selectedModule;
    private string _verificationText = string.Empty;
    private string _disclaimerText = string.Empty;
    private string _readinessSummary = "Нет данных";
    private string _readinessDetailText = string.Empty;

    public ExamCenterViewModel(
        IExamBlueprintRepository examRepository,
        ILearningProgressRepository learningProgressRepository,
        Action<string> navigate)
    {
        _examRepository = examRepository;
        _compatibilityRepository = learningProgressRepository;
        _navigate = navigate;
        Levels = new ObservableCollection<ExamLevelOption>(
        [
            new(null, "Все уровни"),
            new("A1", "A1 · Первый сертификат"),
            new("A2", "A2 · Бытовое общение"),
            new("B1", "B1 · Самостоятельность"),
            new("B2", "B2 · Учёба и работа"),
            new("C1", "C1 · Продвинутый"),
            new("C2", "C2 · Точное владение")
        ]);
        _selectedLevel = Levels[0];
        OpenSourceCommand = new ParameterizedRelayCommand(OpenSource, parameter => parameter is ExamSourceLink source && CanOpen(source.Url));
        OpenWritingPracticeCommand = new RelayCommand(() => _navigate("telc"));
        OpenLearningPathCommand = new RelayCommand(() => _navigate("path"));
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
    }

    public ExamCenterViewModel(
        IExamBlueprintRepository examRepository,
        IAttemptRepository attemptRepository,
        Action<string> navigate)
    {
        _examRepository = examRepository;
        _attemptRepository = attemptRepository;
        _navigate = navigate;
        Levels = new ObservableCollection<ExamLevelOption>(
        [
            new(null, "Все уровни"),
            new("A1", "A1 · Первый сертификат"),
            new("A2", "A2 · Бытовое общение"),
            new("B1", "B1 · Самостоятельность"),
            new("B2", "B2 · Учёба и работа"),
            new("C1", "C1 · Продвинутый"),
            new("C2", "C2 · Точное владение")
        ]);
        _selectedLevel = Levels[0];
        OpenSourceCommand = new ParameterizedRelayCommand(OpenSource, parameter => parameter is ExamSourceLink source && CanOpen(source.Url));
        OpenWritingPracticeCommand = new RelayCommand(() => _navigate("telc"));
        OpenLearningPathCommand = new RelayCommand(() => _navigate("path"));
        RefreshCommand = new AsyncRelayCommand(InitializeAsync);
    }

    public ObservableCollection<ExamLevelOption> Levels { get; }
    public ObservableCollection<ExamBlueprint> Exams { get; } = [];
    public ObservableCollection<ExamSegmentViewModel> Segments { get; } = [];
    public ObservableCollection<ExamReadinessItemViewModel> Readiness { get; } = [];
    public ObservableCollection<ExamSourceLink> Sources { get; } = [];
    public ObservableCollection<ExamTargetOption> Targets { get; } = [];
    public ObservableCollection<ExamModuleOption> Modules { get; } = [];
    public ICommand OpenSourceCommand { get; }
    public ICommand OpenWritingPracticeCommand { get; }
    public ICommand OpenLearningPathCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ExamLevelOption? SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (SetProperty(ref _selectedLevel, value))
            {
                ApplyFilter();
            }
        }
    }

    public ExamBlueprint? SelectedExam
    {
        get => _selectedExam;
        set
        {
            if (SetProperty(ref _selectedExam, value))
            {
                UpdateSelectedExam();
            }
        }
    }

    public ExamTargetOption? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                UpdateReadiness();
            }
        }
    }

    public ExamModuleOption? SelectedModule
    {
        get => _selectedModule;
        set
        {
            if (SetProperty(ref _selectedModule, value))
            {
                UpdateReadiness();
            }
        }
    }

    public string VerificationText { get => _verificationText; private set => SetProperty(ref _verificationText, value); }
    public string DisclaimerText { get => _disclaimerText; private set => SetProperty(ref _disclaimerText, value); }
    public bool HasExam => SelectedExam is not null;
    public string ProviderText => SelectedExam?.ProviderName ?? string.Empty;
    public string LevelText => SelectedExam is null ? string.Empty : string.Join(" / ", SelectedExam.Levels);
    public string WorkingTimeText => SelectedExam is null
        ? string.Empty
        : SelectedExam.TotalSessionMinutes == SelectedExam.TotalWorkingMinutes
            ? $"Рабочее время: около {SelectedExam.TotalWorkingMinutes} мин."
            : $"Рабочее время: {SelectedExam.TotalWorkingMinutes} мин. · с подготовкой и перерывом: около {SelectedExam.TotalSessionMinutes} мин.";
    public string ScoringSummary => SelectedExam?.ScoringSummary ?? string.Empty;
    public string ReadinessSummary { get => _readinessSummary; private set => SetProperty(ref _readinessSummary, value); }
    public string ReadinessDetailText { get => _readinessDetailText; private set => SetProperty(ref _readinessDetailText, value); }

    public async Task InitializeAsync()
    {
        var catalogTask = _examRepository.LoadAsync();
        var attemptsTask = LoadAttemptsAsync();
        await Task.WhenAll(catalogTask, attemptsTask);
        var catalog = await catalogTask;
        _attempts = await attemptsTask;
        _allExams.Clear();
        _allExams.AddRange(catalog.Exams);
        VerificationText = $"Форматы сверены {catalog.LastVerified:dd.MM.yyyy} · {catalog.Exams.Count} маршрутов";
        DisclaimerText = catalog.ReadinessDisclaimer;
        ApplyFilter();
    }

    public Task ActivateAsync() => InitializeAsync();

    private void ApplyFilter()
    {
        var previousId = SelectedExam?.Id;
        Exams.Clear();
        foreach (var exam in _allExams.Where(exam => SelectedLevel?.Level is null ||
                     exam.Levels.Contains(SelectedLevel.Level, StringComparer.OrdinalIgnoreCase)))
        {
            Exams.Add(exam);
        }
        SelectedExam = Exams.FirstOrDefault(exam => exam.Id == previousId) ?? Exams.FirstOrDefault();
    }

    private void UpdateSelectedExam()
    {
        Segments.Clear();
        Sources.Clear();
        Readiness.Clear();
        Targets.Clear();
        Modules.Clear();
        if (SelectedExam is null)
        {
            RaiseExamProperties();
            return;
        }

        foreach (var segment in SelectedExam.Segments)
        {
            var details = new List<string> { $"{segment.Parts} ч." };
            if (segment.Items is { } items)
            {
                details.Add($"{items} заданий");
            }
            if (segment.PreparationMinutes > 0)
            {
                details.Add($"подготовка {segment.PreparationMinutes} мин.");
            }
            if (segment.IsPairFormat)
            {
                details.Add("в паре");
            }
            else if (segment.IsGroupFormat)
            {
                details.Add("в группе");
            }
            else if (segment.IsIndividualFormat || segment.IsRecordedComputerFormat)
            {
                details.Add("индивидуально");
            }
            if (segment.IsDurationPerParticipant)
            {
                details.Add("время на участника");
            }
            details.Add(string.Join(", ", segment.TaskFamilies.Take(3).Select(TaskLabel)));
            Segments.Add(new ExamSegmentViewModel(
                SegmentLabel(segment.Id),
                string.Join(" · ", segment.Skills.Select(SkillLabel)),
                segment.IsApproximate ? $"≈ {segment.DurationMinutes} мин." : $"{segment.DurationMinutes} мин.",
                string.Join(" · ", details)));
        }
        foreach (var source in SelectedExam.Sources)
        {
            Sources.Add(source);
        }

        foreach (var levelText in SelectedExam.Levels)
        {
            if (GermanLevelExtensions.TryParse(levelText, out var level) && level.IsCefrLevel())
            {
                Targets.Add(new ExamTargetOption(level, $"Цель {level}"));
            }
        }
        Modules.Add(new ExamModuleOption(null, "Полный экзамен"));
        foreach (var segment in SelectedExam.Segments)
        {
            Modules.Add(new ExamModuleOption(segment.Id, SegmentLabel(segment.Id)));
        }
        _selectedTarget = Targets.OrderByDescending(item => item.Level).FirstOrDefault();
        OnPropertyChanged(nameof(SelectedTarget));
        _selectedModule = SelectedExam.Scoring.Kind == ExamScoringKind.IndependentModules
            ? Modules.Skip(1).FirstOrDefault()
            : Modules.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedModule));
        UpdateReadiness();

        RaiseExamProperties();
    }

    private void UpdateReadiness()
    {
        Readiness.Clear();
        if (SelectedExam is null || SelectedTarget is null)
        {
            ReadinessSummary = "Нет данных";
            ReadinessDetailText = string.Empty;
            RaiseExamProperties();
            return;
        }

        var target = new ExamReadinessTarget(
            SelectedTarget.Level,
            SelectedModule?.ModuleId,
            SelectedExam.Scoring.Kind == ExamScoringKind.BandPerSkill ? "TDN4" : null);
        var readiness = new ExamPolicyService().Evaluate(SelectedExam, target, _attempts);
        var directModulePolicy = SelectedExam.Scoring.Kind is ExamScoringKind.IndependentModules or ExamScoringKind.BandPerSkill;
        foreach (var module in readiness.Modules)
        {
            Readiness.Add(new ExamReadinessItemViewModel(
                module.Title,
                $"Подтверждений: {module.EvidenceCount}; с таймером: {module.TimedEvidenceCount}",
                module.Band is null ? $"Результат: {module.Score:P0}" : $"Результат: {module.Band}",
                directModulePolicy
                    ? module.MeetsPolicy ? "Порог выполнен" : "Нужна практика"
                    : module.EvidenceCount > 0 ? "Учтено" : "Нет данных"));
        }
        ReadinessSummary = readiness.IsReady
            ? "Готовность подтверждена двумя пробными экзаменами"
            : readiness.PolicySatisfied
                ? $"Порог выполнен · пробные с запасом {readiness.BufferedPassingMockCount}/2"
                : "Порог выбранного формата пока не выполнен";
        ReadinessDetailText = string.Join(" ", readiness.MissingRequirements);
        RaiseExamProperties();
    }

    private async Task<IReadOnlyList<AttemptEvent>> LoadAttemptsAsync()
    {
        if (_attemptRepository is not null)
        {
            return await _attemptRepository.GetAsync();
        }
        if (_compatibilityRepository is null)
        {
            return [];
        }
        var legacy = await _compatibilityRepository.GetAllAsync();
        return legacy.Select((item, index) => new AttemptEvent(
            DeterministicGuid($"exam|{item.CompletedAtUtc:O}|{index}"),
            $"legacy.exam.{item.Level.ToString().ToLowerInvariant()}.{index}",
            1,
            item.Level,
            item.Skill,
            item.ExerciseType,
            AttemptDirection.NotApplicable,
            item.Score,
            item.Mode,
            item.CompletedAtUtc,
            item.CompletedAtUtc,
            item.SessionId ?? DeterministicGuid($"session|{item.CompletedAtUtc:O}|{index}"),
            "legacy-exam-v1",
            EvidenceQuality.HistoricalAggregate,
            item.ObjectiveId,
            item.WasTimed,
            item.Mode == AssessmentMode.MockExam ? "legacy-unknown-exam" : null)).ToArray();
    }

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private void RaiseExamProperties()
    {
        OnPropertyChanged(nameof(HasExam));
        OnPropertyChanged(nameof(ProviderText));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(WorkingTimeText));
        OnPropertyChanged(nameof(ScoringSummary));
        OnPropertyChanged(nameof(ReadinessSummary));
    }

    private static void OpenSource(object? parameter)
    {
        if (parameter is not ExamSourceLink source || !CanOpen(source.Url))
        {
            return;
        }
        Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
    }

    private static bool CanOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        return ApprovedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));
    }

    private static string SegmentLabel(string id) => id switch
    {
        "reading" => "Чтение",
        "listening" => "Аудирование",
        "writing" => "Письмо",
        "speaking" => "Говорение",
        "reading-writing" => "Чтение и письмо",
        "reading-language-elements" => "Чтение и языковые элементы",
        "listening-integrated-writing" => "Аудирование и интегрированное письмо",
        _ => id.Replace('-', ' ')
    };

    private static string SkillLabel(string value) => value switch
    {
        "reading" => "чтение",
        "listening" => "аудирование",
        "writing" => "письмо",
        "speaking" => "говорение",
        "interaction" => "взаимодействие",
        "pronunciation" => "произношение",
        "language-elements" => "языковые элементы",
        _ => value
    };

    private static string TaskLabel(string value) => value.Replace('-', ' ');
}

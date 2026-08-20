using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;

namespace WortBruecke.App.ViewModels;

public sealed record ExamLevelOption(string? Level, string Label);
public sealed record ExamSegmentViewModel(string Title, string Skills, string Duration, string Tasks);
public sealed record ExamReadinessItemViewModel(string Title, string Evidence, string Score, string State);

public sealed class ExamCenterViewModel : ObservableObject
{
    private static readonly HashSet<string> ApprovedHosts =
        ["goethe.de", "telc.net", "testdaf.de", "bamf.de"];

    private readonly IExamBlueprintRepository _examRepository;
    private readonly ILearningProgressRepository _learningProgressRepository;
    private readonly Action<string> _navigate;
    private readonly List<ExamBlueprint> _allExams = [];
    private IReadOnlyList<LearningAttempt> _attempts = [];
    private ExamLevelOption? _selectedLevel;
    private ExamBlueprint? _selectedExam;
    private string _verificationText = string.Empty;
    private string _disclaimerText = string.Empty;

    public ExamCenterViewModel(
        IExamBlueprintRepository examRepository,
        ILearningProgressRepository learningProgressRepository,
        Action<string> navigate)
    {
        _examRepository = examRepository;
        _learningProgressRepository = learningProgressRepository;
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
    }

    public ObservableCollection<ExamLevelOption> Levels { get; }
    public ObservableCollection<ExamBlueprint> Exams { get; } = [];
    public ObservableCollection<ExamSegmentViewModel> Segments { get; } = [];
    public ObservableCollection<ExamReadinessItemViewModel> Readiness { get; } = [];
    public ObservableCollection<ExamSourceLink> Sources { get; } = [];
    public ICommand OpenSourceCommand { get; }
    public ICommand OpenWritingPracticeCommand { get; }
    public ICommand OpenLearningPathCommand { get; }

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

    public string VerificationText { get => _verificationText; private set => SetProperty(ref _verificationText, value); }
    public string DisclaimerText { get => _disclaimerText; private set => SetProperty(ref _disclaimerText, value); }
    public bool HasExam => SelectedExam is not null;
    public string ProviderText => SelectedExam?.ProviderName ?? string.Empty;
    public string LevelText => SelectedExam is null ? string.Empty : string.Join(" / ", SelectedExam.Levels);
    public string WorkingTimeText => SelectedExam is null ? string.Empty : $"Рабочее время: около {SelectedExam.TotalWorkingMinutes} мин.";
    public string ScoringSummary => SelectedExam?.ScoringSummary ?? string.Empty;
    public string ReadinessSummary => Readiness.Count == 0
        ? "Нет данных"
        : $"Готово разделов: {Readiness.Count(item => item.State == "Готов")} из {Readiness.Count}";

    public async Task InitializeAsync()
    {
        var catalogTask = _examRepository.LoadAsync();
        var attemptsTask = _learningProgressRepository.GetAllAsync();
        await Task.WhenAll(catalogTask, attemptsTask);
        var catalog = await catalogTask;
        _attempts = await attemptsTask;
        _allExams.Clear();
        _allExams.AddRange(catalog.Exams);
        VerificationText = $"Форматы сверены {catalog.LastVerified:dd.MM.yyyy} · {catalog.Exams.Count} маршрутов";
        DisclaimerText = catalog.ReadinessDisclaimer;
        ApplyFilter();
    }

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
        if (SelectedExam is null)
        {
            RaiseExamProperties();
            return;
        }

        foreach (var segment in SelectedExam.Segments)
        {
            Segments.Add(new ExamSegmentViewModel(
                SegmentLabel(segment.Id),
                string.Join(" · ", segment.Skills.Select(SkillLabel)),
                segment.IsApproximate ? $"≈ {segment.DurationMinutes} мин." : $"{segment.DurationMinutes} мин.",
                $"{segment.Parts} ч. · {string.Join(", ", segment.TaskFamilies.Take(3).Select(TaskLabel))}"));
        }
        foreach (var source in SelectedExam.Sources)
        {
            Sources.Add(source);
        }

        if (GermanLevelExtensions.TryParse(SelectedExam.Levels.FirstOrDefault(), out var level) && level.IsCefrLevel())
        {
            var genericExam = GermanCurriculum.CreateGenericFourSkillExam(
                $"readiness-{SelectedExam.Id}",
                SelectedExam.Name,
                level);
            var readiness = new LearningProgressService().EvaluateExamReadiness(genericExam, _attempts);
            foreach (var section in readiness.Sections)
            {
                Readiness.Add(new ExamReadinessItemViewModel(
                    section.Definition.Title,
                    $"Попытки: {section.EvidenceCount}/{section.Definition.MinimumEvidenceCount}; с таймером: {section.TimedEvidenceCount}/{section.Definition.MinimumTimedEvidenceCount}",
                    $"Последний средний результат: {section.RecentScore:P0}",
                    section.IsReady ? "Готов" : "Нужна практика"));
            }
        }

        RaiseExamProperties();
    }

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

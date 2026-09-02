using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;

namespace WortBruecke.App.ViewModels;

public sealed record SkillProgressCard(
    LanguageSkill Skill,
    string Title,
    int AttemptCount,
    int DistinctItemCount,
    double AverageScore,
    DateTimeOffset? LastAttemptUtc)
{
    public string ScoreText => AttemptCount == 0 ? "Нет данных" : $"{AverageScore:P0}";
    public string DetailText => AttemptCount == 0
        ? "Начните с доступного задания"
        : $"{AttemptCount} попыток · {DistinctItemCount} разных заданий";
}

public sealed class ProgressViewModel : ObservableObject
{
    private readonly ICourseCatalogRepository _catalog;
    private readonly ICourseProgressRepository _courseProgress;
    private readonly IAttemptRepository _attempts;
    private readonly IReviewStateRepository _reviews;
    private readonly IClock _clock;
    private string _currentLevel = "A0 · Pre-A1";
    private string _weakestSkill = "Недостаточно данных";
    private string _errorMessage = string.Empty;
    private int _completedCourseLessons;
    private int _totalCourseLessons;
    private int _courseEvidenceCount;
    private int _totalAttempts;
    private int _weekAttempts;
    private int _dueCount;
    private double _overallCompletion;
    private bool _isBusy;

    public ProgressViewModel(
        ICourseCatalogRepository catalog,
        ICourseProgressRepository courseProgress,
        IAttemptRepository attempts,
        IReviewStateRepository reviews,
        IClock? clock = null,
        Action<string>? navigate = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _courseProgress = courseProgress ?? throw new ArgumentNullException(nameof(courseProgress));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
        _clock = clock ?? SystemClock.Instance;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, error =>
        {
            ErrorMessage = error.UserMessage;
            OnPropertyChanged(nameof(HasError));
        });
        StartReviewCommand = new RelayCommand(() => navigate?.Invoke("trainer"), () => navigate is not null);
        OpenCoursesCommand = new RelayCommand(() => navigate?.Invoke("path"), () => navigate is not null);
    }

    public ObservableCollection<SkillProgressCard> Skills { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand StartReviewCommand { get; }
    public RelayCommand OpenCoursesCommand { get; }
    public string CurrentLevel { get => _currentLevel; private set => SetProperty(ref _currentLevel, value); }
    public string WeakestSkill { get => _weakestSkill; private set => SetProperty(ref _weakestSkill, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public int CompletedCourseLessons { get => _completedCourseLessons; private set => SetProperty(ref _completedCourseLessons, value); }
    public int TotalCourseLessons { get => _totalCourseLessons; private set => SetProperty(ref _totalCourseLessons, value); }
    public string CourseProgressText => $"{CompletedCourseLessons} из {TotalCourseLessons} уроков A0–A2";
    public int TotalAttempts { get => _totalAttempts; private set => SetProperty(ref _totalAttempts, value); }
    public int WeekAttempts { get => _weekAttempts; private set => SetProperty(ref _weekAttempts, value); }
    public int DueCount { get => _dueCount; private set => SetProperty(ref _dueCount, value); }
    public double OverallCompletion { get => _overallCompletion; private set => SetProperty(ref _overallCompletion, value); }
    public bool HasData => _courseEvidenceCount > 0 || TotalAttempts > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Task InitializeAsync() => RefreshAsync(CancellationToken.None);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(HasError));
        try
        {
            var catalog = await _catalog.LoadAsync(cancellationToken);
            var courses = catalog.Track.Courses
                .Where(course =>
                    course.Availability == CourseAvailability.Published &&
                    course.Level is >= GermanLevel.A0 and <= GermanLevel.A2)
                .OrderBy(course => course.Order)
                .ToArray();
            var courseSummaries = new List<CourseProgressSummary>(courses.Length);
            foreach (var course in courses)
            {
                var progress = await _courseProgress.GetCourseAsync(course.Id, cancellationToken);
                var lessonIds = course.Units
                    .OrderBy(unit => unit.Order)
                    .SelectMany(unit => unit.Lessons.OrderBy(lesson => lesson.Order))
                    .Select(lesson => CoursePathViewModel.LessonNodeId(lesson.Id))
                    .ToHashSet(StringComparer.Ordinal);
                var lessonProgress = progress
                    .Where(item => lessonIds.Contains(item.NodeId))
                    .ToArray();
                var evidence = progress
                    .Where(item => item.Status != CourseNodeStatus.NotStarted)
                    .ToArray();
                courseSummaries.Add(new(
                    course,
                    lessonIds.Count,
                    lessonProgress.Count(item => item.Status >= CourseNodeStatus.Completed),
                    evidence.Length,
                    evidence.Length == 0 ? null : evidence.Max(item => item.UpdatedAtUtc)));
            }

            var all = await _attempts.GetAsync(cancellationToken: cancellationToken);
            var due = await _reviews.GetDueAsync(_clock.UtcNow, 500, cancellationToken);
            TotalCourseLessons = courseSummaries.Sum(item => item.TotalLessons);
            CompletedCourseLessons = courseSummaries.Sum(item => item.CompletedLessons);
            _courseEvidenceCount = courseSummaries.Sum(item => item.EvidenceCount);
            var currentCourse = courseSummaries
                .Where(item => item.LatestEvidenceUtc is not null)
                .OrderByDescending(item => item.LatestEvidenceUtc)
                .ThenByDescending(item => item.Course.Order)
                .FirstOrDefault()
                ?? courseSummaries.FirstOrDefault(item => item.CompletedLessons < item.TotalLessons)
                ?? courseSummaries.LastOrDefault();
            CurrentLevel = currentCourse is null ? "—" : LevelLabel(currentCourse.Course.Level);
            OverallCompletion = TotalCourseLessons == 0 ? 0 : (double)CompletedCourseLessons / TotalCourseLessons;
            TotalAttempts = all.Count;
            WeekAttempts = all.Count(item => item.CompletedAtUtc >= _clock.UtcNow.AddDays(-7));
            DueCount = due.Count(item =>
                item.ContentKey.StartsWith("core.word.", StringComparison.Ordinal) ||
                item.ContentKey.StartsWith("core.sentence.", StringComparison.Ordinal));
            var cards = Enum.GetValues<LanguageSkill>()
                .Select(skill =>
                {
                    var events = all.Where(item => item.Skill == skill)
                        .OrderByDescending(item => item.CompletedAtUtc)
                        .Take(20)
                        .ToArray();
                    return new SkillProgressCard(
                        skill,
                        SkillTitle(skill),
                        events.Length,
                        events.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count(),
                        events.Length == 0 ? 0 : events.Average(item => item.Score),
                        events.FirstOrDefault()?.CompletedAtUtc);
                })
                .ToArray();
            Skills.Clear();
            foreach (var card in cards)
            {
                Skills.Add(card);
            }
            WeakestSkill = cards.Where(item => item.AttemptCount > 0)
                               .OrderBy(item => item.AverageScore)
                               .ThenBy(item => item.Title, StringComparer.Ordinal)
                               .FirstOrDefault()?.Title
                           ?? "Недостаточно данных";
            OnPropertyChanged(nameof(CourseProgressText));
            OnPropertyChanged(nameof(HasData));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SkillTitle(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Vocabulary => "Лексика",
        LanguageSkill.Grammar => "Грамматика",
        LanguageSkill.Reading => "Чтение",
        LanguageSkill.Listening => "Аудирование",
        LanguageSkill.Writing => "Письмо",
        LanguageSkill.Speaking => "Говорение",
        LanguageSkill.Mediation => "Медиация",
        _ => skill.ToString()
    };

    private static string LevelLabel(GermanLevel level) => level == GermanLevel.A0 ? "A0 · Pre-A1" : level.ToString();

    private sealed record CourseProgressSummary(
        CourseDefinition Course,
        int TotalLessons,
        int CompletedLessons,
        int EvidenceCount,
        DateTimeOffset? LatestEvidenceUtc);
}

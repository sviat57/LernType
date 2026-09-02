using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;

namespace WortBruecke.App.ViewModels;

public sealed class CourseCardViewModel : ObservableObject
{
    private bool _isSelected;

    internal CourseCardViewModel(
        CourseDefinition definition,
        int completedLessons,
        int totalLessons,
        Func<Task> select)
    {
        Definition = definition;
        CompletedLessons = completedLessons;
        TotalLessons = totalLessons;
        SelectCommand = new AsyncRelayCommand(select, () => definition.Availability == CourseAvailability.Published);
    }

    internal CourseDefinition Definition { get; }
    public string Id => Definition.Id;
    public string LevelText => Definition.Level == GermanLevel.A0 ? "A0" : Definition.Level.ToString();
    public string Title => Definition.Title;
    public string Subtitle => Definition.Subtitle;
    public int CompletedLessons { get; }
    public int TotalLessons { get; }
    public bool IsPublished => Definition.Availability == CourseAvailability.Published;
    public string StateText => IsPublished ? "Опубликован" : "Готовится";
    public string ProgressText => IsPublished ? $"{CompletedLessons} из {TotalLessons} уроков" : "Контент другого уровня не подставляется";
    public double ProgressValue => TotalLessons == 0 ? 0 : 100d * CompletedLessons / TotalLessons;
    public AsyncRelayCommand SelectCommand { get; }
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
}

public sealed class CourseLessonNodeViewModel
{
    internal CourseLessonNodeViewModel(
        CourseLessonDefinition definition,
        int sequence,
        bool isUnlocked,
        bool isCompleted,
        double bestScore,
        Func<Task> open)
    {
        Definition = definition;
        Sequence = sequence;
        IsUnlocked = isUnlocked;
        IsCompleted = isCompleted;
        BestScore = bestScore;
        OpenCommand = new AsyncRelayCommand(open, () => isUnlocked);
    }

    internal CourseLessonDefinition Definition { get; }
    public int Sequence { get; }
    public string NumberText => Sequence.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    public string Title => Definition.Title;
    public string Outcome => Definition.Outcome;
    public bool IsUnlocked { get; }
    public bool IsCompleted { get; }
    public double BestScore { get; }
    public string MetaText => $"{Definition.EstimatedMinutes} мин · 6 шагов";
    public string StatusText => IsCompleted ? $"Пройдено · {BestScore:P0}" : IsUnlocked ? "Доступен" : "После предыдущего урока";
    public string AutomationName => IsUnlocked ? $"Открыть урок {Sequence}: {Title}" : $"Урок {Sequence} заблокирован: {Title}";
    public AsyncRelayCommand OpenCommand { get; }
}

public sealed record CourseUnitSectionViewModel(
    string NumberText,
    string Title,
    string Outcome,
    IReadOnlyList<CourseLessonNodeViewModel> Lessons);

/// <summary>Loads the offline track, selects a course and exposes its sequential lesson map.</summary>
public sealed class CoursePathViewModel : ObservableObject
{
    private readonly ICourseCatalogRepository _catalogRepository;
    private readonly ICourseProgressRepository _progressRepository;
    private readonly Func<CourseLessonLaunch, Task> _openLesson;
    private readonly Func<CourseExamLaunch, Task> _openExam;
    private CourseCatalog? _catalog;
    private CourseDefinition? _selectedCourse;
    private IReadOnlyDictionary<string, CourseNodeProgress> _selectedProgress =
        new Dictionary<string, CourseNodeProgress>(StringComparer.Ordinal);
    private bool _isBusy;
    private string _statusText = "Загружаем локальный каталог…";

    public CoursePathViewModel(
        ICourseCatalogRepository catalogRepository,
        ICourseProgressRepository progressRepository,
        Func<CourseLessonLaunch, Task> openLesson,
        Func<CourseExamLaunch, Task> openExam)
    {
        _catalogRepository = catalogRepository ?? throw new ArgumentNullException(nameof(catalogRepository));
        _progressRepository = progressRepository ?? throw new ArgumentNullException(nameof(progressRepository));
        _openLesson = openLesson ?? throw new ArgumentNullException(nameof(openLesson));
        _openExam = openExam ?? throw new ArgumentNullException(nameof(openExam));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, SetError);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => SelectedCourse is not null && !IsBusy, SetError);
        OpenExamCommand = new AsyncRelayCommand(OpenExamAsync, () => IsExamUnlocked && !IsBusy, SetError);
    }

    public ObservableCollection<CourseCardViewModel> Courses { get; } = [];
    public ObservableCollection<CourseUnitSectionViewModel> Units { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ContinueCommand { get; }
    public AsyncRelayCommand OpenExamCommand { get; }

    public string TrackTitle => _catalog?.Track.Title ?? "Немецкий с нуля";
    public string TrackMeta => _catalog is null ? "Локальный каталог" : $"Редакция {_catalog.Revision} · оригинальные материалы LernType";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                ContinueCommand.RaiseCanExecuteChanged();
                OpenExamCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CourseDefinition? SelectedCourse
    {
        get => _selectedCourse;
        private set
        {
            if (!SetProperty(ref _selectedCourse, value)) return;
            OnPropertyChanged(nameof(HasSelectedCourse));
            OnPropertyChanged(nameof(SelectedCourseTitle));
            OnPropertyChanged(nameof(SelectedCourseSubtitle));
            OnPropertyChanged(nameof(SelectedCourseOutcome));
            OnPropertyChanged(nameof(SelectedCourseLevel));
            OnPropertyChanged(nameof(ExamTitle));
            OnPropertyChanged(nameof(ContinueButtonText));
            ContinueCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedCourse => SelectedCourse is not null;
    public string SelectedCourseTitle => SelectedCourse?.Title ?? "Выберите курс";
    public string SelectedCourseSubtitle => SelectedCourse?.Subtitle ?? string.Empty;
    public string SelectedCourseOutcome => SelectedCourse?.Outcome ?? string.Empty;
    public string SelectedCourseLevel => SelectedCourse?.Level == GermanLevel.A0 ? "A0 · Pre-A1" : SelectedCourse?.Level.ToString() ?? string.Empty;
    public int CompletedLessonCount => _selectedCourse is null ? 0 :
        _selectedCourse.Units.SelectMany(unit => unit.Lessons).Count(lesson => IsCompleted(LessonNodeId(lesson.Id)));
    public int TotalLessonCount => _selectedCourse?.Units.Sum(unit => unit.Lessons.Count) ?? 0;
    public double CourseProgressValue => TotalLessonCount == 0 ? 0 : 100d * CompletedLessonCount / TotalLessonCount;
    public string CourseProgressText => $"{CompletedLessonCount} из {TotalLessonCount} уроков";
    public bool IsExamUnlocked => SelectedCourse?.Exam is not null && TotalLessonCount > 0 && CompletedLessonCount == TotalLessonCount;
    public bool HasExamAttempt => SelectedCourse?.Exam is not null && HasProgress(ExamNodeId(SelectedCourse.Exam.Id));
    public bool IsExamPassed => SelectedCourse?.Exam is not null &&
        Status(ExamNodeId(SelectedCourse.Exam.Id)) == CourseNodeStatus.Passed;
    public string ExamTitle => SelectedCourse?.Exam?.Title ?? "Внутренний экзамен LernType";
    public string ExamStatusText => IsExamPassed
        ? $"Пройден · лучший результат {BestScore(ExamNodeId(SelectedCourse!.Exam!.Id)):P0}"
        : HasExamAttempt
            ? $"Пока не пройден · лучший результат {BestScore(ExamNodeId(SelectedCourse!.Exam!.Id)):P0} · нужно 75%"
        : IsExamUnlocked
            ? "Доступен после всех уроков · проходной результат 75%"
            : "Откроется после всех восьми уроков";
    public string ExamButtonText => HasExamAttempt ? "Повторить экзамен" : "Начать экзамен";
    public string ContinueButtonText => CompletedLessonCount == 0 ? "Начать курс" : CompletedLessonCount < TotalLessonCount ? "Продолжить курс" : "Повторить последний урок";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is not null) return;
        await LoadAsync(cancellationToken);
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default) => LoadAsync(cancellationToken);

    private async Task RefreshAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            _catalog ??= await _catalogRepository.LoadAsync(cancellationToken);
            var previousId = SelectedCourse?.Id;
            Courses.Clear();
            foreach (var definition in _catalog.Track.Courses.OrderBy(course => course.Order))
            {
                var progress = definition.Availability == CourseAvailability.Published
                    ? await _progressRepository.GetCourseAsync(definition.Id, cancellationToken)
                    : [];
                var completed = definition.Units.SelectMany(unit => unit.Lessons)
                    .Count(lesson => progress.Any(item => item.NodeId == LessonNodeId(lesson.Id) && IsCompleted(item.Status)));
                CourseCardViewModel? card = null;
                card = new CourseCardViewModel(
                    definition,
                    completed,
                    definition.Units.Sum(unit => unit.Lessons.Count),
                    () => SelectCourseAsync(card!.Definition, CancellationToken.None));
                Courses.Add(card);
            }

            var selected = _catalog.Track.Courses.FirstOrDefault(course => course.Id == previousId && course.Availability == CourseAvailability.Published)
                ?? _catalog.Track.Courses.First(course => course.Availability == CourseAvailability.Published);
            await SelectCourseAsync(selected, cancellationToken);
            StatusText = "Курсы готовы офлайн. A0, A1 и A2 можно выбирать свободно.";
            OnPropertyChanged(nameof(TrackTitle));
            OnPropertyChanged(nameof(TrackMeta));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SelectCourseAsync(CourseDefinition course, CancellationToken cancellationToken)
    {
        if (course.Availability != CourseAvailability.Published) return;
        SelectedCourse = course;
        foreach (var card in Courses) card.IsSelected = card.Id == course.Id;
        var progress = await _progressRepository.GetCourseAsync(course.Id, cancellationToken);
        _selectedProgress = progress.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        BuildUnits(course);
        RaiseProgressProperties();
    }

    private void BuildUnits(CourseDefinition course)
    {
        Units.Clear();
        var sequence = 0;
        var previousCompleted = true;
        foreach (var unit in course.Units.OrderBy(item => item.Order))
        {
            var lessons = new List<CourseLessonNodeViewModel>();
            foreach (var lesson in unit.Lessons.OrderBy(item => item.Order))
            {
                sequence++;
                var nodeId = LessonNodeId(lesson.Id);
                var completed = IsCompleted(nodeId);
                var unlocked = previousCompleted || completed;
                var launch = new CourseLessonLaunch(course.Id, unit.Id, lesson.Id);
                lessons.Add(new CourseLessonNodeViewModel(
                    lesson,
                    sequence,
                    unlocked,
                    completed,
                    BestScore(nodeId),
                    () => OpenLessonWithResumeAsync(launch)));
                previousCompleted = completed;
            }
            Units.Add(new CourseUnitSectionViewModel(
                unit.Order.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                unit.Title,
                unit.Outcome,
                lessons));
        }
    }

    private async Task OpenLessonWithResumeAsync(CourseLessonLaunch launch)
    {
        var resume = await _progressRepository.GetResumeAsync(launch.CourseId);
        var target = resume is not null && resume.UnitId == launch.UnitId && resume.LessonId == launch.LessonId
            ? launch with { StepId = resume.StepId }
            : launch;
        await _openLesson(target);
    }

    private async Task ContinueAsync(CancellationToken cancellationToken)
    {
        if (SelectedCourse is null) return;
        var all = SelectedCourse.Units.OrderBy(unit => unit.Order)
            .SelectMany(unit => unit.Lessons.OrderBy(lesson => lesson.Order).Select(lesson => (unit, lesson)))
            .ToArray();
        var target = all.FirstOrDefault(item => !IsCompleted(LessonNodeId(item.lesson.Id)));
        if (target.lesson is null) target = all[^1];
        await OpenLessonWithResumeAsync(new(SelectedCourse.Id, target.unit.Id, target.lesson.Id));
    }

    private async Task OpenExamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedCourse?.Exam is null || !IsExamUnlocked) return;
        await _openExam(new(SelectedCourse.Id, SelectedCourse.Exam.Id));
    }

    private bool IsCompleted(string nodeId) =>
        _selectedProgress.TryGetValue(nodeId, out var progress) && IsCompleted(progress.Status);

    private bool HasProgress(string nodeId) => _selectedProgress.ContainsKey(nodeId);

    private CourseNodeStatus Status(string nodeId) =>
        _selectedProgress.TryGetValue(nodeId, out var progress) ? progress.Status : CourseNodeStatus.NotStarted;

    private static bool IsCompleted(CourseNodeStatus status) => status >= CourseNodeStatus.Completed;

    private double BestScore(string nodeId) =>
        _selectedProgress.TryGetValue(nodeId, out var progress) ? progress.BestScore : 0;

    private void RaiseProgressProperties()
    {
        OnPropertyChanged(nameof(CompletedLessonCount));
        OnPropertyChanged(nameof(TotalLessonCount));
        OnPropertyChanged(nameof(CourseProgressValue));
        OnPropertyChanged(nameof(CourseProgressText));
        OnPropertyChanged(nameof(IsExamUnlocked));
        OnPropertyChanged(nameof(HasExamAttempt));
        OnPropertyChanged(nameof(IsExamPassed));
        OnPropertyChanged(nameof(ExamStatusText));
        OnPropertyChanged(nameof(ExamButtonText));
        OnPropertyChanged(nameof(ContinueButtonText));
        OpenExamCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
    }

    private void SetError(OperationError error) => StatusText = error.UserMessage;
    public static string LessonNodeId(string lessonId) => $"lesson:{lessonId}";
    public static string ExamNodeId(string examId) => $"exam:{examId}";
}

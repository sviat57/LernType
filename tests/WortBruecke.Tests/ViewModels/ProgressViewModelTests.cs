using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;

namespace WortBruecke.Tests.ViewModels;

public sealed class ProgressViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MostRecentlyTouchedCourse_DrivesCurrentLevelWhileCompletionAggregatesAllLessons()
    {
        var courseProgress = new MemoryCourseProgressRepository(
        [
            Completed("a0", "a0-lesson-1", Now.AddDays(-3)),
            Completed("a1", "a1-lesson-1", Now.AddDays(-2)),
            Completed("a1", "a1-lesson-2", Now.AddDays(-2)),
            InProgress("a2", "a2-lesson-1", Now),
            Completed("b1", "b1-lesson-1", Now.AddDays(1))
        ]);
        var attempts = new MemoryAttemptRepository([Attempt(GermanLevel.C2, LanguageSkill.Vocabulary, 0.4)]);
        var reviews = new MemoryReviewStateRepository(
        [
            Review("core.word.fixture"),
            Review("book.word.fixture")
        ]);
        var viewModel = new ProgressViewModel(
            new MemoryCourseCatalogRepository(Catalog()),
            courseProgress,
            attempts,
            reviews,
            new FixedClock(Now));

        await viewModel.InitializeAsync();

        Assert.Equal("A2", viewModel.CurrentLevel);
        Assert.Equal(3, viewModel.CompletedCourseLessons);
        Assert.Equal(6, viewModel.TotalCourseLessons);
        Assert.Equal(0.5, viewModel.OverallCompletion);
        Assert.Equal("3 из 6 уроков A0–A2", viewModel.CourseProgressText);
        Assert.True(viewModel.HasData);
        Assert.Equal(1, viewModel.TotalAttempts);
        Assert.Equal(1, viewModel.WeekAttempts);
        Assert.Equal(1, viewModel.DueCount);
        Assert.Equal("Лексика", viewModel.WeakestSkill);
        Assert.Equal(1, viewModel.Skills.Single(card => card.Skill == LanguageSkill.Vocabulary).AttemptCount);
    }

    [Fact]
    public async Task InProgressCourseNode_IsVisibleWithoutSupplementaryAttempts()
    {
        var destinations = new List<string>();
        var viewModel = new ProgressViewModel(
            new MemoryCourseCatalogRepository(Catalog()),
            new MemoryCourseProgressRepository([InProgress("a0", "a0-lesson-1")]),
            new MemoryAttemptRepository([]),
            new MemoryReviewStateRepository([]),
            new FixedClock(Now),
            destinations.Add);

        await viewModel.InitializeAsync();
        viewModel.OpenCoursesCommand.Execute(null);
        viewModel.StartReviewCommand.Execute(null);

        Assert.True(viewModel.HasData);
        Assert.Equal("A0 · Pre-A1", viewModel.CurrentLevel);
        Assert.Equal(0, viewModel.CompletedCourseLessons);
        Assert.Equal(0, viewModel.OverallCompletion);
        Assert.Equal(["path", "trainer"], destinations);
    }

    [Fact]
    public async Task CompletingEveryPublishedCourse_EndsAtA2AndReportsOneHundredPercent()
    {
        var completed = new[] { "a0", "a1", "a2" }
            .SelectMany(courseId => new[] { 1, 2 }.Select(index => Completed(courseId, $"{courseId}-lesson-{index}")))
            .ToArray();
        var viewModel = new ProgressViewModel(
            new MemoryCourseCatalogRepository(Catalog()),
            new MemoryCourseProgressRepository(completed),
            new MemoryAttemptRepository([]),
            new MemoryReviewStateRepository([]),
            new FixedClock(Now));

        await viewModel.InitializeAsync();

        Assert.Equal("A2", viewModel.CurrentLevel);
        Assert.Equal(6, viewModel.CompletedCourseLessons);
        Assert.Equal(1, viewModel.OverallCompletion);
    }

    private static CourseCatalog Catalog() => new(
        1,
        new CourseTrackDefinition(
            "fixture-track",
            "Fixture",
            [
                Course("a0", 1, GermanLevel.A0, CourseAvailability.Published),
                Course("a1", 2, GermanLevel.A1, CourseAvailability.Published),
                Course("a2", 3, GermanLevel.A2, CourseAvailability.Published),
                Course("b1", 4, GermanLevel.B1, CourseAvailability.Planned)
            ]));

    private static CourseDefinition Course(
        string id,
        int order,
        GermanLevel level,
        CourseAvailability availability) => new(
            id,
            order,
            level,
            $"Course {id}",
            "Fixture",
            "Fixture",
            availability,
            [
                new CourseUnitDefinition(
                    $"{id}-unit",
                    1,
                    "Fixture unit",
                    "Fixture",
                    [
                        Lesson($"{id}-lesson-1", 1),
                        Lesson($"{id}-lesson-2", 2)
                    ])
            ],
            null);

    private static CourseLessonDefinition Lesson(string id, int order) => new(
        id,
        order,
        $"Lesson {order}",
        "Fixture",
        10,
        []);

    private static CourseNodeProgress Completed(
        string courseId,
        string lessonId,
        DateTimeOffset? updatedAtUtc = null) => new(
        courseId,
        CoursePathViewModel.LessonNodeId(lessonId),
        CourseNodeStatus.Completed,
        1,
        1,
        updatedAtUtc ?? Now);

    private static CourseNodeProgress InProgress(
        string courseId,
        string lessonId,
        DateTimeOffset? updatedAtUtc = null) => new(
        courseId,
        CoursePathViewModel.LessonNodeId(lessonId),
        CourseNodeStatus.InProgress,
        0.5,
        1,
        updatedAtUtc ?? Now);

    private static AttemptEvent Attempt(GermanLevel level, LanguageSkill skill, double score) => new(
        Guid.NewGuid(),
        "supplementary.fixture",
        1,
        level,
        skill,
        ExerciseType.VocabularyRecall,
        AttemptDirection.GermanToRussian,
        score,
        AssessmentMode.Practice,
        Now.AddMinutes(-1),
        Now,
        Guid.NewGuid(),
        "fixture-v1",
        EvidenceQuality.Deterministic);

    private static ReviewState Review(string contentKey) => new(
        contentKey,
        1,
        5,
        Now.AddMinutes(-1),
        Now.AddDays(-1),
        1,
        0,
        "fixture-v1");

    private sealed class MemoryCourseCatalogRepository(CourseCatalog catalog) : ICourseCatalogRepository
    {
        public Task<CourseCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog);
    }

    private sealed class MemoryCourseProgressRepository(IEnumerable<CourseNodeProgress> progress) : ICourseProgressRepository
    {
        private readonly List<CourseNodeProgress> _progress = [.. progress];

        public Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(
            string courseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CourseNodeProgress>>(
                _progress.Where(item => item.CourseId == courseId).ToArray());

        public Task UpsertAsync(CourseNodeProgress progress, CancellationToken cancellationToken = default)
        {
            _progress.RemoveAll(item => item.CourseId == progress.CourseId && item.NodeId == progress.NodeId);
            _progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task<CourseResumeState?> GetResumeAsync(
            string courseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CourseResumeState?>(null);

        public Task SaveResumeAsync(CourseResumeState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MemoryAttemptRepository(IReadOnlyList<AttemptEvent> attempts) : IAttemptRepository
    {
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<AttemptEvent>> GetAsync(
            AttemptQuery? query = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(attempts);
    }

    private sealed class MemoryReviewStateRepository(IReadOnlyList<ReviewState> due) : IReviewStateRepository
    {
        public Task<ReviewState?> GetAsync(string contentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(due.FirstOrDefault(item => item.ContentKey == contentKey));

        public Task<IReadOnlyList<ReviewState>> GetDueAsync(
            DateTimeOffset asOfUtc,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReviewState>>(due.Take(limit).ToArray());

        public Task UpsertAsync(ReviewState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

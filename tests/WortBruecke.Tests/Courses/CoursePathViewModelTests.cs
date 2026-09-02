using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Courses;

public sealed class CoursePathViewModelTests
{
    [Fact]
    public async Task Courses_AreFreelySelectableWhileLessonsAndExamUnlockSequentially()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var progress = new MemoryCourseProgressRepository();
        CourseLessonLaunch? lessonLaunch = null;
        CourseExamLaunch? examLaunch = null;
        var viewModel = new CoursePathViewModel(
            catalogRepository,
            progress,
            launch => { lessonLaunch = launch; return Task.CompletedTask; },
            launch => { examLaunch = launch; return Task.CompletedTask; });

        await viewModel.InitializeAsync();

        Assert.Equal("A0", viewModel.SelectedCourseLevel.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
        var a0Lessons = viewModel.Units.SelectMany(unit => unit.Lessons).ToArray();
        Assert.True(a0Lessons[0].IsUnlocked);
        Assert.False(a0Lessons[1].IsUnlocked);
        Assert.False(viewModel.IsExamUnlocked);
        Assert.False(viewModel.Courses.Single(card => card.LevelText == "B1").SelectCommand.CanExecute(null));

        await viewModel.Courses.Single(card => card.LevelText == "A1").SelectCommand.ExecuteAsync();
        Assert.Equal("A1", viewModel.SelectedCourseLevel);
        var course = viewModel.SelectedCourse!;
        var lessons = course.Units.OrderBy(unit => unit.Order)
            .SelectMany(unit => unit.Lessons.OrderBy(lesson => lesson.Order))
            .ToArray();

        progress.Items.Add(Completed(course.Id, CoursePathViewModel.LessonNodeId(lessons[0].Id)));
        await viewModel.ActivateAsync();
        var nodes = viewModel.Units.SelectMany(unit => unit.Lessons).ToArray();
        Assert.True(nodes[1].IsUnlocked);
        Assert.False(nodes[2].IsUnlocked);

        foreach (var lesson in lessons.Skip(1))
        {
            progress.Items.Add(Completed(course.Id, CoursePathViewModel.LessonNodeId(lesson.Id)));
        }
        await viewModel.ActivateAsync();
        Assert.True(viewModel.IsExamUnlocked);
        await viewModel.OpenExamCommand.ExecuteAsync();
        Assert.Equal(course.Id, examLaunch!.CourseId);
        Assert.Equal(course.Exam!.Id, examLaunch.ExamId);

        await viewModel.Units[0].Lessons[0].OpenCommand.ExecuteAsync();
        Assert.Equal(course.Id, lessonLaunch!.CourseId);
        Assert.Equal(lessons[0].Id, lessonLaunch.LessonId);
    }

    private static CourseNodeProgress Completed(string courseId, string nodeId) =>
        new(courseId, nodeId, CourseNodeStatus.Completed, 1, 1, DateTimeOffset.UtcNow);

    private sealed class MemoryCourseProgressRepository : ICourseProgressRepository
    {
        public List<CourseNodeProgress> Items { get; } = [];
        private readonly Dictionary<string, CourseResumeState> _resume = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(
            string courseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CourseNodeProgress>>(Items.Where(item => item.CourseId == courseId).ToArray());

        public Task UpsertAsync(CourseNodeProgress progress, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.CourseId == progress.CourseId && item.NodeId == progress.NodeId);
            Items.Add(progress);
            return Task.CompletedTask;
        }

        public Task<CourseResumeState?> GetResumeAsync(string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_resume.GetValueOrDefault(courseId));

        public Task SaveResumeAsync(CourseResumeState state, CancellationToken cancellationToken = default)
        {
            _resume[state.CourseId] = state;
            return Task.CompletedTask;
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LernType.sln"))) return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("LernType.sln was not found.");
    }
}

using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;
using WortBruecke.Infrastructure.Audio;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Courses;

public sealed class CourseLessonViewModelTests
{
    [Fact]
    public async Task LessonTask_ExposesPromptAndCourseViewBindsIt()
    {
        var repositoryRoot = FindRepositoryRoot();
        var contentRoot = Path.Combine(repositoryRoot, "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var catalog = await catalogRepository.LoadAsync();
        var candidate = (from courseItem in catalog.Track.Courses
                         where courseItem.Availability == CourseAvailability.Published
                         from unitItem in courseItem.Units
                         from lessonItem in unitItem.Lessons
                         from stepItem in lessonItem.Steps
                         where stepItem.Kind == CourseStepKind.Reading && stepItem.Task is not null
                         select (Course: courseItem, Unit: unitItem, Lesson: lessonItem, Step: stepItem)).First();
        var viewModel = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { });

        await viewModel.PrepareAsync(new(
            candidate.Course.Id,
            candidate.Unit.Id,
            candidate.Lesson.Id,
            candidate.Step.Id));

        Assert.True(viewModel.HasTaskPrompt);
        Assert.Equal(candidate.Step.Task!.Prompt, viewModel.TaskPrompt);
        Assert.NotEqual(viewModel.Instruction, viewModel.TaskPrompt);
        var xaml = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot,
            "src",
            "WortBruecke.App",
            "Views",
            "CourseLessonView.xaml"));
        Assert.Contains("Text=\"{Binding TaskPrompt}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteLesson_RecordsOnlyFourActiveTasksAndUnlocksLessonAtSixtyPercent()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var progress = new MemoryCourseProgressRepository();
        var attempts = new MemoryAttemptRepository();
        var viewModel = new CourseLessonViewModel(catalogRepository, progress, attempts, () => { });
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var unit = course.Units.OrderBy(item => item.Order).First();
        var lesson = unit.Lessons.OrderBy(item => item.Order).First();

        await viewModel.PrepareAsync(new(course.Id, unit.Id, lesson.Id));
        while (!viewModel.IsFlowComplete)
        {
            if (!viewModel.HasTask)
            {
                await viewModel.NextCommand.ExecuteAsync();
                continue;
            }

            if (viewModel.IsSpeakingTask)
            {
                await viewModel.CompleteSpeakingCommand.ExecuteAsync();
            }
            else if (viewModel.ShowOptions)
            {
                viewModel.SelectedOption = viewModel.CurrentTask!.Answer;
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            else
            {
                viewModel.AnswerText = viewModel.CurrentTask!.Answer!;
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            await viewModel.NextCommand.ExecuteAsync();
        }

        Assert.Equal(4, attempts.Items.Count);
        Assert.All(attempts.Items, attempt => Assert.StartsWith($"course.{course.Id}.lesson.{lesson.Id}.", attempt.ContentKey));
        Assert.DoesNotContain(attempts.Items, attempt => attempt.ContentKey.Contains("brief", StringComparison.Ordinal));
        Assert.DoesNotContain(attempts.Items, attempt => attempt.ContentKey.Contains("rule", StringComparison.Ordinal));
        var completion = progress.Items.Single(item => item.NodeId == CoursePathViewModel.LessonNodeId(lesson.Id));
        Assert.Equal(CourseNodeStatus.Completed, completion.Status);
        Assert.Equal(1, completion.BestScore);
        Assert.True(viewModel.HasResult);
        var resume = await progress.GetResumeAsync(course.Id);
        Assert.Equal(lesson.Steps.OrderBy(step => step.Order).First().Id, resume!.StepId);

        await viewModel.RestartCommand.ExecuteAsync();
        await CompleteCurrentFlowAsync(viewModel, useCorrectAnswers: false);
        completion = progress.Items.Single(item => item.NodeId == CoursePathViewModel.LessonNodeId(lesson.Id));
        Assert.Equal(CourseNodeStatus.Completed, completion.Status);
        Assert.Equal(1, completion.BestScore);
        Assert.Equal(2, completion.AttemptCount);
        Assert.Contains("уже пройден ранее", viewModel.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CourseExam_RequiresSpeakingEvidenceButExcludesSelfRatingFromDeterministicScore()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var progress = new MemoryCourseProgressRepository();
        var attempts = new MemoryAttemptRepository();
        var viewModel = new CourseLessonViewModel(catalogRepository, progress, attempts, () => { });
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var exam = Assert.IsType<CourseExamDefinition>(course.Exam);
        var deterministicCount = exam.Questions.Count(question => question.Kind != CourseTaskKind.SelfRecordedSpeech);
        var submittedWrongAnswer = false;

        await viewModel.PrepareExamAsync(new(course.Id, exam.Id));
        while (!viewModel.IsFlowComplete)
        {
            if (viewModel.IsSpeakingTask)
            {
                await viewModel.CompleteSpeakingCommand.ExecuteAsync();
            }
            else if (viewModel.ShowOptions)
            {
                viewModel.SelectedOption = submittedWrongAnswer
                    ? viewModel.CurrentExamQuestion!.Answer
                    : "заведомо неверный ответ";
                submittedWrongAnswer = true;
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            else
            {
                viewModel.AnswerText = submittedWrongAnswer
                    ? viewModel.CurrentExamQuestion!.Answer!
                    : "заведомо неверный ответ";
                submittedWrongAnswer = true;
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            await viewModel.NextCommand.ExecuteAsync();
        }

        var result = progress.Items.Single(item => item.NodeId == CoursePathViewModel.ExamNodeId(exam.Id));
        Assert.Equal((deterministicCount - 1d) / deterministicCount, result.BestScore, precision: 10);
        Assert.Equal(CourseNodeStatus.Passed, result.Status);
        Assert.Equal(2, attempts.Items.Count(item => item.EvidenceQuality == EvidenceQuality.SelfReported));
        Assert.Equal(deterministicCount, attempts.Items.Count(item => item.EvidenceQuality == EvidenceQuality.Deterministic));

        await viewModel.RestartCommand.ExecuteAsync();
        await CompleteCurrentFlowAsync(viewModel, useCorrectAnswers: false);
        result = progress.Items.Single(item => item.NodeId == CoursePathViewModel.ExamNodeId(exam.Id));
        Assert.Equal(CourseNodeStatus.Passed, result.Status);
        Assert.Equal((deterministicCount - 1d) / deterministicCount, result.BestScore, precision: 10);
        Assert.Equal(2, result.AttemptCount);
        Assert.Contains("уже пройден ранее", viewModel.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExamListening_UsesHiddenAudioStimulusInsteadOfSpeakingTheAnswer()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var audio = new FakeAudioService();
        var viewModel = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            audio);
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var exam = Assert.IsType<CourseExamDefinition>(course.Exam);

        await viewModel.PrepareExamAsync(new(course.Id, exam.Id));
        while (!viewModel.IsListeningTask)
        {
            if (viewModel.ShowOptions) viewModel.SelectedOption = viewModel.CurrentExamQuestion!.Answer;
            else viewModel.AnswerText = viewModel.CurrentExamQuestion!.Answer!;
            await viewModel.SubmitCommand.ExecuteAsync();
            await viewModel.NextCommand.ExecuteAsync();
        }

        var question = Assert.IsType<CourseExamQuestionDefinition>(viewModel.CurrentExamQuestion);
        Assert.True(viewModel.ShowAudioTask);
        Assert.DoesNotContain(question.AudioText!, question.Prompt, StringComparison.Ordinal);
        await viewModel.ListenCommand.ExecuteAsync();
        Assert.Equal(question.AudioText, audio.LastSpokenText);
        Assert.NotEqual(question.Answer, audio.LastSpokenText);
    }

    [Fact]
    public async Task SpeakingModel_IsVisibleWithoutGermanVoiceAndAfterVoicedSelfCheck()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var catalog = await catalogRepository.LoadAsync();
        var candidate = (from courseItem in catalog.Track.Courses
                         where courseItem.Availability == CourseAvailability.Published
                         from unitItem in courseItem.Units
                         from lessonItem in unitItem.Lessons
                         from stepItem in lessonItem.Steps
                         where stepItem.Kind == CourseStepKind.ListeningSpeaking
                               && stepItem.Task?.ModelAnswer is not null
                               && stepItem.GermanText != stepItem.Task.ModelAnswer
                         select (Course: courseItem, Unit: unitItem, Lesson: lessonItem, Step: stepItem)).First();
        var noVoice = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            new FakeAudioService(hasGermanVoice: false));

        await noVoice.PrepareAsync(new(candidate.Course.Id, candidate.Unit.Id, candidate.Lesson.Id, candidate.Step.Id));

        Assert.False(noVoice.HasGermanVoice);
        Assert.True(noVoice.ShowSpeakingModelText);
        Assert.Equal(candidate.Step.Task!.ModelAnswer, noVoice.SpeakingModelText);
        Assert.NotEqual(candidate.Step.GermanText, noVoice.SpeakingModelText);

        var voiced = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            new FakeAudioService(hasInputDevice: false));
        await voiced.PrepareAsync(new(candidate.Course.Id, candidate.Unit.Id, candidate.Lesson.Id, candidate.Step.Id));
        Assert.True(voiced.HasGermanVoice);
        Assert.False(voiced.ShowSpeakingModelText);
        await voiced.CompleteSpeakingCommand.ExecuteAsync();
        Assert.True(voiced.ShowSpeakingModelText);
        Assert.Equal(candidate.Step.Task.ModelAnswer, voiced.SpeakingModelText);
    }

    [Fact]
    public async Task LeavingSpeakingStep_StopsAndDeletesAnActiveRecording()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var catalog = await catalogRepository.LoadAsync();
        var candidate = (from courseItem in catalog.Track.Courses
                         where courseItem.Availability == CourseAvailability.Published
                         from unitItem in courseItem.Units
                         from lessonItem in unitItem.Lessons
                         from stepItem in lessonItem.Steps
                         where stepItem.Kind == CourseStepKind.ListeningSpeaking
                               && stepItem.Task?.ModelAnswer is not null
                               && stepItem.GermanText != stepItem.Task.ModelAnswer
                         select (Course: courseItem, Unit: unitItem, Lesson: lessonItem, Step: stepItem)).First();
        var root = Path.Combine(Path.GetTempPath(), "LernTypeCourseAudio", Guid.NewGuid().ToString("N"));
        var store = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var audio = new FakeAudioService();
        var viewModel = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            audio,
            store);
        try
        {
            await viewModel.PrepareAsync(new(candidate.Course.Id, candidate.Unit.Id, candidate.Lesson.Id, candidate.Step.Id));
            await viewModel.ListenCommand.ExecuteAsync();
            Assert.Equal(candidate.Step.Task!.ModelAnswer, audio.LastSpokenText);
            Assert.NotEqual(candidate.Step.GermanText, audio.LastSpokenText);
            await viewModel.StartRecordingCommand.ExecuteAsync();
            var recordingPath = audio.LastRecordingPath;
            Assert.True(viewModel.IsRecording);
            Assert.True(File.Exists(recordingPath));

            await viewModel.CancelActiveWorkAsync();

            Assert.False(viewModel.IsRecording);
            Assert.Equal(1, audio.StopRecordingCount);
            Assert.False(File.Exists(recordingPath));
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingFailure_UnlocksMandatorySpeakingSelfCheck()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var unit = course.Units.OrderBy(item => item.Order).First();
        var lesson = unit.Lessons.OrderBy(item => item.Order).First();
        var speaking = lesson.Steps.Single(step => step.Kind == CourseStepKind.ListeningSpeaking);
        var root = Path.Combine(Path.GetTempPath(), "LernTypeCourseAudioFailure", Guid.NewGuid().ToString("N"));
        var store = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var audio = new FakeAudioService(failRecording: true);
        var viewModel = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            audio,
            store);
        try
        {
            await viewModel.PrepareAsync(new(course.Id, unit.Id, lesson.Id, speaking.Id));
            await viewModel.StartRecordingCommand.ExecuteAsync();

            Assert.True(viewModel.HasMicrophone);
            Assert.False(viewModel.HasRecording);
            Assert.True(viewModel.CompleteSpeakingCommand.CanExecute(null));
            await viewModel.CompleteSpeakingCommand.ExecuteAsync();
            Assert.True(viewModel.IsTaskAnswered);
            Assert.Contains("без записи", viewModel.Feedback, StringComparison.Ordinal);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StopRecordingFailure_CleansTemporaryFileAndUnlocksSpeakingSelfCheck()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var unit = course.Units.OrderBy(item => item.Order).First();
        var lesson = unit.Lessons.OrderBy(item => item.Order).First();
        var speaking = lesson.Steps.Single(step => step.Kind == CourseStepKind.ListeningSpeaking);
        var root = Path.Combine(Path.GetTempPath(), "LernTypeCourseAudioStopFailure", Guid.NewGuid().ToString("N"));
        var store = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var audio = new FakeAudioService(failStopping: true);
        var viewModel = new CourseLessonViewModel(
            catalogRepository,
            new MemoryCourseProgressRepository(),
            new MemoryAttemptRepository(),
            () => { },
            audio,
            store);
        try
        {
            await viewModel.PrepareAsync(new(course.Id, unit.Id, lesson.Id, speaking.Id));
            await viewModel.StartRecordingCommand.ExecuteAsync();
            var recordingPath = audio.LastRecordingPath;
            Assert.True(File.Exists(recordingPath));

            await viewModel.StopRecordingCommand.ExecuteAsync();

            Assert.False(viewModel.IsRecording);
            Assert.False(viewModel.HasRecording);
            Assert.False(File.Exists(recordingPath));
            Assert.True(viewModel.CompleteSpeakingCommand.CanExecute(null));
            Assert.Contains("без файла", viewModel.AudioStatus, StringComparison.Ordinal);
            await viewModel.CompleteSpeakingCommand.ExecuteAsync();
            Assert.True(viewModel.IsTaskAnswered);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_RestoresEarlierScoresAndCannotBypassLessonThreshold()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalogRepository = new JsonCourseCatalogRepository(contentRoot);
        var progress = new MemoryCourseProgressRepository();
        var attempts = new MemoryAttemptRepository();
        var catalog = await catalogRepository.LoadAsync();
        var course = catalog.Track.Courses.Single(item => item.Level == GermanLevel.A0);
        var unit = course.Units.OrderBy(item => item.Order).First();
        var lesson = unit.Lessons.OrderBy(item => item.Order).First();
        var firstRun = new CourseLessonViewModel(catalogRepository, progress, attempts, () => { });

        await firstRun.PrepareAsync(new(course.Id, unit.Id, lesson.Id));
        await firstRun.NextCommand.ExecuteAsync();
        firstRun.AnswerText = "заведомо неверный ответ";
        await firstRun.SubmitCommand.ExecuteAsync();
        await firstRun.NextCommand.ExecuteAsync();
        firstRun.SelectedOption = "заведомо неверный ответ";
        await firstRun.SubmitCommand.ExecuteAsync();
        await firstRun.NextCommand.ExecuteAsync();
        await firstRun.CompleteSpeakingCommand.ExecuteAsync();
        await firstRun.NextCommand.ExecuteAsync();

        var resume = Assert.IsType<CourseResumeState>(await progress.GetResumeAsync(course.Id));
        Assert.Equal(3, resume.TaskScores.Count);
        Assert.Equal(2, resume.TaskScores.Count(item => item.Value == 0));
        Assert.Single(resume.SelfReportedTaskKeys);

        var resumed = new CourseLessonViewModel(catalogRepository, progress, attempts, () => { });
        await resumed.PrepareAsync(new(course.Id, unit.Id, lesson.Id, resume.StepId));
        await resumed.NextCommand.ExecuteAsync();
        if (resumed.ShowOptions) resumed.SelectedOption = resumed.CurrentTask!.Answer;
        else resumed.AnswerText = resumed.CurrentTask!.Answer!;
        await resumed.SubmitCommand.ExecuteAsync();
        await resumed.NextCommand.ExecuteAsync();

        var completion = progress.Items.Single(item => item.NodeId == CoursePathViewModel.LessonNodeId(lesson.Id));
        Assert.Equal(CourseNodeStatus.InProgress, completion.Status);
        Assert.Equal(1d / 3d, completion.BestScore, precision: 10);
        Assert.Contains("нужно 60%", resumed.ResultText, StringComparison.Ordinal);
    }

    private static async Task CompleteCurrentFlowAsync(CourseLessonViewModel viewModel, bool useCorrectAnswers)
    {
        while (!viewModel.IsFlowComplete)
        {
            if (!viewModel.HasTask)
            {
                await viewModel.NextCommand.ExecuteAsync();
                continue;
            }

            if (viewModel.IsSpeakingTask)
            {
                await viewModel.CompleteSpeakingCommand.ExecuteAsync();
            }
            else if (viewModel.ShowOptions)
            {
                viewModel.SelectedOption = useCorrectAnswers
                    ? viewModel.IsExamMode ? viewModel.CurrentExamQuestion!.Answer : viewModel.CurrentTask!.Answer
                    : "заведомо неверный ответ";
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            else
            {
                viewModel.AnswerText = useCorrectAnswers
                    ? viewModel.IsExamMode ? viewModel.CurrentExamQuestion!.Answer! : viewModel.CurrentTask!.Answer!
                    : "заведомо неверный ответ";
                await viewModel.SubmitCommand.ExecuteAsync();
            }
            await viewModel.NextCommand.ExecuteAsync();
        }
    }

    private sealed class MemoryAttemptRepository : IAttemptRepository
    {
        public List<AttemptEvent> Items { get; } = [];
        public Task<bool> AppendAsync(AttemptEvent attempt, CancellationToken cancellationToken = default)
        {
            Items.Add(attempt);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<AttemptEvent>> GetAsync(AttemptQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttemptEvent>>(Items);
    }

    private sealed class FakeAudioService(
        bool failRecording = false,
        bool failStopping = false,
        bool hasGermanVoice = true,
        bool hasInputDevice = true) : IAudioPracticeService
    {
        private string? _recordingPath;
        private bool _recording;
        public string LastRecordingPath => _recordingPath ?? string.Empty;
        public string LastSpokenText { get; private set; } = string.Empty;
        public int StopRecordingCount { get; private set; }
        public IReadOnlyList<AudioInputDevice> GetInputDevices() =>
            hasInputDevice ? [new(0, "Test microphone")] : [];
        public IReadOnlyList<InstalledSpeechVoice> GetSpeechVoices() =>
            hasGermanVoice ? [new("Test German", "de-DE", true)] : [];
        public Task SpeakAsync(string text, string cultureCode = "de-DE", int rate = 0, CancellationToken cancellationToken = default)
        {
            LastSpokenText = text;
            return Task.CompletedTask;
        }
        public async Task StartRecordingAsync(string targetWavePath, int deviceNumber = 0, CancellationToken cancellationToken = default)
        {
            if (failRecording) throw new InvalidOperationException("Test recording failure");
            _recordingPath = targetWavePath;
            _recording = true;
            Directory.CreateDirectory(Path.GetDirectoryName(targetWavePath)!);
            await File.WriteAllBytesAsync(targetWavePath, [82, 73, 70, 70], cancellationToken);
        }
        public Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            if (failStopping) throw new IOException("Test stop recording failure");
            if (!_recording) throw new InvalidOperationException("Recording is not active");
            _recording = false;
            StopRecordingCount++;
            return Task.FromResult(_recordingPath!);
        }
        public Task PlayAsync(string wavePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StopPlayback() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryCourseProgressRepository : ICourseProgressRepository
    {
        public List<CourseNodeProgress> Items { get; } = [];
        private readonly Dictionary<string, CourseResumeState> _resume = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(string courseId, CancellationToken cancellationToken = default) =>
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

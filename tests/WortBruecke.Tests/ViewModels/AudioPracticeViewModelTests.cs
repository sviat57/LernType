using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.Tests.ViewModels;

public sealed class AudioPracticeViewModelTests
{
    [Fact]
    public async Task CheckTranscript_RecordsDeterministicListeningEvidence()
    {
        var audio = new FakeAudioService();
        var attempts = new MemoryAttemptRepository();
        await using var viewModel = new AudioPracticeViewModel(audio, attempts, new ImmediateClock());
        await viewModel.InitializeAsync();
        viewModel.Transcript = viewModel.SelectedPrompt.GermanText;

        await viewModel.CheckTranscriptCommand.ExecuteAsync();

        var attempt = Assert.Single(attempts.Items);
        Assert.Equal(LanguageSkill.Listening, attempt.Skill);
        Assert.Equal(ExerciseType.Dictation, attempt.ExerciseFamily);
        Assert.Equal(EvidenceQuality.Deterministic, attempt.EvidenceQuality);
        Assert.Equal(1, attempt.Score);
    }

    [Fact]
    public async Task TimedRecording_IsTemporaryAndSelfRatingIsLowQualityEvidence()
    {
        var audio = new FakeAudioService();
        var attempts = new MemoryAttemptRepository();
        var recordingRoot = Path.Combine(Path.GetTempPath(), "LernType.Tests", Guid.NewGuid().ToString("N"));
        var recordingStore = new TemporaryAudioRecordingStore(recordingRoot, [TimeSpan.Zero]);
        var viewModel = new AudioPracticeViewModel(audio, attempts, new ImmediateClock(), recordingStore);
        await viewModel.InitializeAsync();

        await viewModel.StartTimedRecordingCommand.ExecuteAsync();
        Assert.True(viewModel.HasRecording);
        Assert.True(File.Exists(audio.LastRecordingPath));

        await viewModel.RateSpeakingCommand.ExecuteAsync("0.75");
        var attempt = Assert.Single(attempts.Items);
        Assert.Equal(LanguageSkill.Speaking, attempt.Skill);
        Assert.Equal(EvidenceQuality.SelfReported, attempt.EvidenceQuality);
        Assert.Equal(0.75, attempt.Score);

        await viewModel.DisposeAsync();
        await recordingStore.DisposeAsync();
        Assert.False(File.Exists(audio.LastRecordingPath));
        Assert.False(Directory.Exists(recordingRoot));
    }

    [Fact]
    public async Task Initialize_WithoutGermanVoice_ExplainsMissingCapability()
    {
        await using var viewModel = new AudioPracticeViewModel(new FakeAudioService(hasGermanVoice: false));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasGermanVoice);
        Assert.True(viewModel.HasError);
        Assert.Contains("немецкий голос", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromptChange_WhenImmediateDeleteIsBlocked_LeavesRecordingOwnedBySessionJanitor()
    {
        var root = Path.Combine(Path.GetTempPath(), "LernType.Tests", Guid.NewGuid().ToString("N"));
        var store = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var audio = new FakeAudioService();
        var viewModel = new AudioPracticeViewModel(audio, clock: new ImmediateClock(), recordingStore: store);
        try
        {
            await viewModel.InitializeAsync();
            await viewModel.StartTimedRecordingCommand.ExecuteAsync();
            var recording = audio.LastRecordingPath;
            using (var playbackLock = new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                viewModel.SelectedPrompt = viewModel.Prompts[1];
                await Task.Delay(25);
                Assert.True(File.Exists(recording));
            }

            await viewModel.DisposeAsync();
            Assert.True(File.Exists(recording));

            await store.DisposeAsync();
            Assert.False(File.Exists(recording));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            await viewModel.DisposeAsync();
            await store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeAudioService(bool hasGermanVoice = true) : IAudioPracticeService
    {
        private string? _path;
        public string LastRecordingPath => _path ?? string.Empty;
        public IReadOnlyList<AudioInputDevice> GetInputDevices() => [new(0, "Test microphone")];
        public IReadOnlyList<InstalledSpeechVoice> GetSpeechVoices() =>
            hasGermanVoice ? [new("Test German", "de-DE", true)] : [new("Test Russian", "ru-RU", true)];
        public Task SpeakAsync(string text, string cultureCode = "de-DE", int rate = 0, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task StartRecordingAsync(string targetWavePath, int deviceNumber = 0, CancellationToken cancellationToken = default)
        {
            _path = targetWavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(targetWavePath)!);
            await File.WriteAllBytesAsync(targetWavePath, [82, 73, 70, 70], cancellationToken);
        }
        public Task<string> StopRecordingAsync(CancellationToken cancellationToken = default) => Task.FromResult(_path!);
        public Task PlayAsync(string wavePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StopPlayback() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class ImmediateClock : IClock
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        public DateTimeOffset UtcNow => _now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _now += delay;
            return Task.CompletedTask;
        }
    }
}

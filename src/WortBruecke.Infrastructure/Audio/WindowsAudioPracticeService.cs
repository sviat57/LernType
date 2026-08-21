using System.Globalization;
using System.Speech.Synthesis;
using NAudio.Wave;
using WortBruecke.Core.Abstractions;

namespace WortBruecke.Infrastructure.Audio;

public sealed class WindowsAudioPracticeService : IAudioPracticeService
{
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _recordingGate = new();
    private readonly object _playbackGate = new();
    private WaveIn? _waveIn;
    private WaveFileWriter? _waveWriter;
    private TaskCompletionSource<string>? _recordingStopped;
    private string? _recordingPath;
    private WaveOut? _waveOut;
    private AudioFileReader? _audioReader;
    private TaskCompletionSource? _playbackStopped;
    private bool _disposed;

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        ThrowIfDisposed();
        var result = new List<AudioInputDevice>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            result.Add(new(index, capabilities.ProductName));
        }
        return result;
    }

    public IReadOnlyList<InstalledSpeechVoice> GetSpeechVoices()
    {
        ThrowIfDisposed();
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Select(voice => new InstalledSpeechVoice(
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.Enabled))
            .OrderBy(voice => voice.CultureCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SpeakAsync(
        string text,
        string cultureCode = "de-DE",
        int rate = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Озвучиваемый текст ограничен 10 000 символами.");
        }
        if (rate is < -10 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        await _speechGate.WaitAsync(cancellationToken);
        try
        {
            using var synthesizer = new SpeechSynthesizer { Rate = rate };
            var culture = CultureInfo.GetCultureInfo(cultureCode);
            var voice = synthesizer.GetInstalledVoices(culture)
                .FirstOrDefault(item => item.Enabled);
            if (voice is null)
            {
                throw new InvalidOperationException(
                    $"В Windows не найден голос {culture.DisplayName}. Установите его в настройках речи.");
            }

            synthesizer.SelectVoice(voice.VoiceInfo.Name);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<SpeakCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                synthesizer.SpeakCompleted -= handler;
                if (args.Error is not null)
                {
                    completion.TrySetException(args.Error);
                }
                else if (args.Cancelled)
                {
                    completion.TrySetCanceled();
                }
                else
                {
                    completion.TrySetResult();
                }
            };
            synthesizer.SpeakCompleted += handler;
            using var registration = cancellationToken.Register(() => synthesizer.SpeakAsyncCancelAll());
            synthesizer.SpeakAsync(text);
            await completion.Task;
        }
        finally
        {
            _speechGate.Release();
        }
    }

    public Task StartRecordingAsync(
        string targetWavePath,
        int deviceNumber = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWavePath);
        if (Path.GetExtension(targetWavePath) is not ".wav")
        {
            throw new ArgumentException("Запись должна сохраняться в WAV-файл.", nameof(targetWavePath));
        }
        if (deviceNumber < 0 || deviceNumber >= WaveIn.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceNumber), "Устройство записи недоступно.");
        }

        lock (_recordingGate)
        {
            if (_waveIn is not null)
            {
                throw new InvalidOperationException("Запись уже запущена.");
            }

            var fullPath = Path.GetFullPath(targetWavePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _recordingPath = fullPath;
            _recordingStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waveWriter = new WaveFileWriter(fullPath, new WaveFormat(16_000, 16, 1));
            _waveIn = new WaveIn
            {
                DeviceNumber = deviceNumber,
                WaveFormat = _waveWriter.WaveFormat,
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            try
            {
                _waveIn.StartRecording();
            }
            catch
            {
                CleanupRecording();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Task<string> completion;
        lock (_recordingGate)
        {
            if (_waveIn is null || _recordingStopped is null)
            {
                throw new InvalidOperationException("Запись ещё не запущена.");
            }
            completion = _recordingStopped.Task;
            _waveIn.StopRecording();
        }
        return await completion.WaitAsync(cancellationToken);
    }

    public async Task PlayAsync(string wavePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        var fullPath = Path.GetFullPath(wavePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Аудиозапись не найдена.", fullPath);
        }

        Task completion;
        lock (_playbackGate)
        {
            StopPlaybackCore();
            _playbackStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _audioReader = new AudioFileReader(fullPath);
            _waveOut = new WaveOut();
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_audioReader);
            completion = _playbackStopped.Task;
            _waveOut.Play();
        }
        using var registration = cancellationToken.Register(StopPlayback);
        await completion.WaitAsync(cancellationToken);
    }

    public void StopPlayback()
    {
        lock (_playbackGate)
        {
            _waveOut?.Stop();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_recordingGate)
        {
            _waveWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        TaskCompletionSource<string>? completion;
        string? path;
        lock (_recordingGate)
        {
            completion = _recordingStopped;
            path = _recordingPath;
            CleanupRecording();
        }
        if (args.Exception is not null)
        {
            completion?.TrySetException(args.Exception);
        }
        else if (path is not null)
        {
            completion?.TrySetResult(path);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        TaskCompletionSource? completion;
        lock (_playbackGate)
        {
            completion = _playbackStopped;
            CleanupPlayback();
        }
        if (args.Exception is not null)
        {
            completion?.TrySetException(args.Exception);
        }
        else
        {
            completion?.TrySetResult();
        }
    }

    private void StopPlaybackCore()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Stop();
        }
        else
        {
            CleanupPlayback();
        }
    }

    private void CleanupRecording()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
        }
        _waveWriter?.Dispose();
        _waveIn = null;
        _waveWriter = null;
        _recordingStopped = null;
        _recordingPath = null;
    }

    private void CleanupPlayback()
    {
        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
            _waveOut.Dispose();
        }
        _audioReader?.Dispose();
        _waveOut = null;
        _audioReader = null;
        _playbackStopped = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopPlayback();
        lock (_recordingGate)
        {
            if (_waveIn is not null)
            {
                _waveIn.StopRecording();
            }
        }
        await _speechGate.WaitAsync();
        _speechGate.Release();
        _speechGate.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

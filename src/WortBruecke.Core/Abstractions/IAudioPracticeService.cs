namespace WortBruecke.Core.Abstractions;

public sealed record AudioInputDevice(int DeviceNumber, string Name);

public sealed record InstalledSpeechVoice(string Name, string CultureCode, bool IsEnabled);

public interface IAudioPracticeService : IAsyncDisposable
{
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    IReadOnlyList<InstalledSpeechVoice> GetSpeechVoices();

    Task SpeakAsync(
        string text,
        string cultureCode = "de-DE",
        int rate = 0,
        CancellationToken cancellationToken = default);

    Task StartRecordingAsync(
        string targetWavePath,
        int deviceNumber = 0,
        CancellationToken cancellationToken = default);

    Task<string> StopRecordingAsync(CancellationToken cancellationToken = default);

    Task PlayAsync(string wavePath, CancellationToken cancellationToken = default);

    void StopPlayback();
}

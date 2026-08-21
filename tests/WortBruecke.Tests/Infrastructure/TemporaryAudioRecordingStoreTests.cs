using WortBruecke.Infrastructure.Audio;

namespace WortBruecke.Tests.Infrastructure;

public sealed class TemporaryAudioRecordingStoreTests
{
    [Fact]
    public async Task DeleteAsync_RetriesUntilTransientPlaybackLockIsReleased()
    {
        var root = CreateRoot();
        var store = new TemporaryAudioRecordingStore(
            root,
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)]);
        try
        {
            var recording = store.CreateRecordingPath();
            await File.WriteAllBytesAsync(recording, [82, 73, 70, 70]);
            var playbackLock = new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.None);

            var deletion = store.DeleteAsync(recording);
            await Task.Delay(15);
            Assert.False(deletion.IsCompleted);
            playbackLock.Dispose();

            Assert.True(await deletion);
            Assert.False(File.Exists(recording));
        }
        finally
        {
            await store.DisposeAsync();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task DisposeAsync_RetainsResponsibilityAfterBoundedFileRetriesAreExhausted()
    {
        var root = CreateRoot();
        var store = new TemporaryAudioRecordingStore(
            root,
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10)]);
        var recording = store.CreateRecordingPath();
        await File.WriteAllBytesAsync(recording, [82, 73, 70, 70]);
        var playbackLock = new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.False(await store.DeleteAsync(recording));
            Assert.True(File.Exists(recording));
        }
        finally
        {
            playbackLock.Dispose();
        }

        await store.DisposeAsync();

        Assert.False(File.Exists(recording));
        Assert.False(Directory.Exists(Path.GetDirectoryName(recording)));
        DeleteTestRoot(root);
    }

    [Fact]
    public async Task CleanupOrphans_DeletesInterruptedAndLegacyFilesButPreservesLiveSession()
    {
        var root = CreateRoot();
        var liveStore = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var janitor = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        try
        {
            var liveRecording = liveStore.CreateRecordingPath();
            await File.WriteAllBytesAsync(liveRecording, [82, 73, 70, 70]);

            var orphanSessionId = Guid.NewGuid().ToString("N");
            var orphanDirectory = Path.Combine(root, orphanSessionId);
            Directory.CreateDirectory(orphanDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(orphanDirectory, $"{Guid.NewGuid():N}.wav"),
                [82, 73, 70, 70]);
            await File.WriteAllTextAsync(Path.Combine(root, $".{orphanSessionId}.active.lock"), "stale");

            var legacyRecording = Path.Combine(root, $"{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(legacyRecording, [82, 73, 70, 70]);

            await janitor.CleanupOrphansAsync();

            Assert.True(File.Exists(liveRecording));
            Assert.False(Directory.Exists(orphanDirectory));
            Assert.False(File.Exists(legacyRecording));
        }
        finally
        {
            await janitor.DisposeAsync();
            await liveStore.DisposeAsync();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_RejectsPathsOutsideOwnedSession()
    {
        var root = CreateRoot();
        await using var store = new TemporaryAudioRecordingStore(root, [TimeSpan.Zero]);
        var outsidePath = Path.Combine(root, $"{Guid.NewGuid():N}.wav");

        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(outsidePath));
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "LernType.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed assertion should not be hidden by best-effort fixture cleanup.
        }
    }
}

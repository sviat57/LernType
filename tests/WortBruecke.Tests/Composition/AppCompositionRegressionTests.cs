using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WortBruecke.App.ViewModels;

namespace WortBruecke.Tests.Composition;

/// <summary>
/// Guards the production composition root without constructing a WPF Application on an xUnit
/// worker thread. These tests intentionally inspect the small composition surface rather than
/// duplicating it in a test-only container.
/// </summary>
public sealed class AppCompositionRegressionTests
{
    [Fact]
    public void ProductionComposition_UsesOneCanonicalAttemptStoreAndPrivacyAwareBookRepository()
    {
        var source = Compact(ReadRepositoryFile("src", "WortBruecke.App", "App.xaml.cs"));

        Assert.Single(Regex.Matches(
            source,
            @"AddSingleton<IAttemptRepository,\s*SqliteAttemptRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Contains(
            "AddSingleton<IReviewStateRepository, SqliteReviewStateRepository>();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<IManagedBackupService, ManagedBackupService>();",
            source,
            StringComparison.Ordinal);
        Assert.Matches(
            @"AddSingleton<IBookRepository>\(services\s*=>\s*new\s+SqliteBookRepository\(\s*services\.GetRequiredService<SqliteDatabase>\(\),\s*services\.GetRequiredService<IManagedBackupService>\(\)\)\)",
            source);

        var mainViewModelFactory = Slice(
            source,
            "AddSingleton<MainViewModel>",
            "AddSingleton<MainWindow>");
        Assert.Single(Regex.Matches(
            mainViewModelFactory,
            @"GetRequiredService<IAttemptRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Single(Regex.Matches(
            mainViewModelFactory,
            @"GetRequiredService<IReviewStateRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
    }

    [Fact]
    public void ProductionComposition_ConnectsCanonicalBookAudioAndProgressRoutes()
    {
        var appSource = Compact(ReadRepositoryFile("src", "WortBruecke.App", "App.xaml.cs"));
        var shellSource = Compact(ReadRepositoryFile(
            "src",
            "WortBruecke.App",
            "ViewModels",
            "MainViewModel.cs"));

        Assert.Contains(
            "AddSingleton<IAudioPracticeService, WindowsAudioPracticeService>();",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<TemporaryAudioRecordingStore>();",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetRequiredService<IAudioPracticeService>()",
            Slice(appSource, "AddSingleton<MainViewModel>", "AddSingleton<MainWindow>"),
            StringComparison.Ordinal);

        // The canonical store is fanned out inside the shell; screens do not construct their own stores.
        Assert.Contains(
            "new BookViewModel(bookRepository, bookVocabularyExtractor, attemptRepository",
            shellSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "new AudioPracticeViewModel( audioPracticeService, attemptRepository, recordingStore: temporaryAudioRecordingStore)",
            shellSource,
            StringComparison.Ordinal);
        Assert.Matches(
            @"new ProgressViewModel\(attemptRepository,\s*reviewStateRepository,\s*navigate:\s*Navigate\)",
            shellSource);
        Assert.Contains("_screens[\"audio\"] = _audio", shellSource, StringComparison.Ordinal);
        Assert.Contains("_screens[\"progress\"] = _progress", shellSource, StringComparison.Ordinal);
        Assert.Contains("_initializers[\"audio\"]", shellSource, StringComparison.Ordinal);
        Assert.Contains("_initializers[\"progress\"]", shellSource, StringComparison.Ordinal);
        Assert.Contains("CreateNav(\"audio\"", shellSource, StringComparison.Ordinal);
        Assert.Contains("CreateNav(\"progress\"", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_LayoutPrerequisiteRunsAfterVisibleShellAndFailureStaysLocal()
    {
        var appSource = Compact(ReadRepositoryFile("src", "WortBruecke.App", "App.xaml.cs"));
        var startup = Slice(appSource, "protected override async void OnStartup", "private IHost BuildHost");

        AssertOrdered(
            startup,
            "window.Show();",
            "await _host.StartAsync();",
            "await InitializeStorageAsync(CancellationToken.None);",
            "ShowKeyboardLayoutPrerequisiteIfNeeded");

        var prerequisite = Slice(
            appSource,
            "private void ShowKeyboardLayoutPrerequisiteIfNeeded",
            "private async Task InitializeStorageAsync");
        Assert.Contains(
            "CheckInstalled(pair.Source.CultureCode, pair.Target.CultureCode)",
            prerequisite,
            StringComparison.Ordinal);
        Assert.Contains("new LayoutSetupWindow(layoutService, pair)", prerequisite, StringComparison.Ordinal);
        Assert.Contains("Owner = MainWindow", prerequisite, StringComparison.Ordinal);
        Assert.Contains("startup.keyboard-layout-check.failed", prerequisite, StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown(", prerequisite, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_RendersShellBeforeStorageAndInitializesScreensOnDemand()
    {
        var appSource = Compact(ReadRepositoryFile("src", "WortBruecke.App", "App.xaml.cs"));
        var shellSource = Compact(ReadRepositoryFile(
            "src",
            "WortBruecke.App",
            "ViewModels",
            "MainViewModel.cs"));

        AssertOrdered(
            appSource,
            "window.Show();",
            "await InitializeStorageAsync(CancellationToken.None);");

        var storageInitialization = Slice(
            appSource,
            "private async Task InitializeStorageAsync",
            "private void OnDispatcherUnhandledException");
        AssertOrdered(
            storageInitialization,
            "await _database.InitializeAsync(cancellationToken);",
            "_viewModel.MarkStorageReady();",
            "await _viewModel.InitializeAsync(cancellationToken);");
        Assert.Contains(
            "_viewModel.ReportStartupFailure(exception);",
            storageInitialization,
            StringComparison.Ordinal);

        var shellInitialization = Slice(
            shellSource,
            "public async Task InitializeAsync",
            "public void MarkStorageReady");
        Assert.Contains(
            "await EnsureInitializedAsync(\"settings\", cancellationToken);",
            shellInitialization,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll", shellInitialization, StringComparison.Ordinal);

        Assert.Contains(
            "if (!IsStorageReady && key is not (\"home\" or \"settings\"))",
            shellSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await EnsureInitializedAsync(key, cancellationToken);",
            shellSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressRoute_ReadOnlyCompletionBindingIsExplicitlyOneWay()
    {
        var completionProperty = typeof(ProgressViewModel).GetProperty(nameof(ProgressViewModel.OverallCompletion));
        Assert.NotNull(completionProperty);
        Assert.False(completionProperty.SetMethod?.IsPublic ?? false);

        var metadata = Assert.IsType<FrameworkPropertyMetadata>(
            ProgressBar.ValueProperty.GetMetadata(typeof(ProgressBar)));
        Assert.True(metadata.BindsTwoWayByDefault);

        var progressView = ReadRepositoryFile(
            "src",
            "WortBruecke.App",
            "Views",
            "ProgressView.xaml");
        Assert.Contains(
            "Value=\"{Binding OverallCompletion, Mode=OneWay}\"",
            progressView,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] pathSegments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathSegments]));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("LernType.sln was not found above the test working directory.");
    }

    private static string Compact(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker was not found after start: {endMarker}");
        return source[start..end];
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Marker is missing or out of order: {marker}");
            previous = current;
        }
    }
}

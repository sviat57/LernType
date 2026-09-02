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
    public void ProductionComposition_UsesCanonicalCourseAttemptAndBackupStores()
    {
        var source = Compact(ReadRepositoryFile("src", "WortBruecke.App", "App.xaml.cs"));

        Assert.Single(Regex.Matches(
            source,
            @"AddSingleton<ICourseCatalogRepository>\(services\s*=>\s*new\s+JsonCourseCatalogRepository\(\s*services\.GetRequiredService<AppPaths>\(\)\.ContentRoot\s*\)\s*\)",
            RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Single(Regex.Matches(
            source,
            @"AddSingleton<ICourseProgressRepository,\s*SqliteCourseProgressRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
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

        string[] removedRegistrations =
        [
            "AddSingleton<IProgressRepository",
            "AddSingleton<ILearningProgressRepository",
            "AddSingleton<IBookRepository",
            "AddSingleton<IBookVocabularyExtractor",
            "AddSingleton<IExamBlueprintRepository",
            "AddSingleton<HttpClient",
            "AddHttpClient",
            "AddSingleton<ILanguageAnalysisService",
        ];
        foreach (var registration in removedRegistrations)
        {
            Assert.DoesNotContain(registration, source, StringComparison.Ordinal);
        }

        var mainViewModelFactory = Slice(
            source,
            "AddSingleton<MainViewModel>",
            "AddSingleton<MainWindow>");
        Assert.Single(Regex.Matches(
            mainViewModelFactory,
            @"GetRequiredService<ICourseCatalogRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Single(Regex.Matches(
            mainViewModelFactory,
            @"GetRequiredService<ICourseProgressRepository>\(\)",
            RegexOptions.CultureInvariant).Cast<Match>());
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
    public void ProductionComposition_ConnectsCourseFlowAndKeepsLegacyToolsOffPublicNavigation()
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
        Assert.Contains(
            "AddSingleton<ICourseCatalogRepository>",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<ICourseProgressRepository, SqliteCourseProgressRepository>();",
            appSource,
            StringComparison.Ordinal);

        // Only the supported course and supplementary drill surfaces are composed by the shell.
        Assert.DoesNotContain("new BookViewModel(", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new ExamCenterViewModel(", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new LearningPathViewModel(", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new LevelStudyViewModel(", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new GrammarViewModel(", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new TelcViewModel(", shellSource, StringComparison.Ordinal);
        Assert.Contains(
            "new AudioPracticeViewModel( audioPracticeService, attemptRepository, recordingStore: temporaryAudioRecordingStore)",
            shellSource,
            StringComparison.Ordinal);
        Assert.Matches(
            @"new ProgressViewModel\(\s*courseCatalogRepository,\s*courseProgressRepository,\s*attemptRepository,\s*reviewStateRepository,\s*navigate:\s*Navigate\)",
            shellSource);
        Assert.Contains("_screens[\"audio\"] = _audio", shellSource, StringComparison.Ordinal);
        Assert.Contains("_screens[\"progress\"] = _progress", shellSource, StringComparison.Ordinal);
        Assert.Contains("_initializers[\"audio\"]", shellSource, StringComparison.Ordinal);
        Assert.Contains("_initializers[\"progress\"]", shellSource, StringComparison.Ordinal);
        Assert.Contains("new CoursePathViewModel(", shellSource, StringComparison.Ordinal);
        Assert.Contains("new CourseLessonViewModel(", shellSource, StringComparison.Ordinal);
        Assert.Contains("CreateNav(\"path\", \"Курсы\"", shellSource, StringComparison.Ordinal);
        Assert.Contains("CreateNav(\"interactive\", \"Интерактивные упражнения\"", shellSource, StringComparison.Ordinal);
        Assert.Contains("CreateNav(\"progress\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNav(\"audio\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNav(\"books\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNav(\"exams\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNav(\"grammar\"", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNav(\"telc\"", shellSource, StringComparison.Ordinal);

        var settingsView = ReadRepositoryFile("src", "WortBruecke.App", "Views", "SettingsView.xaml");
        Assert.DoesNotContain("OpenAI", settingsView, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TELC", settingsView, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API-ключ", settingsView, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void CourseRoutes_PreserveTypedLessonAndExamContext()
    {
        var shellSource = Compact(ReadRepositoryFile(
            "src",
            "WortBruecke.App",
            "ViewModels",
            "MainViewModel.cs"));

        var openLesson = Slice(
            shellSource,
            "private async Task OpenCourseLessonAsync(CourseLessonLaunch launch)",
            "private async Task OpenCourseExamAsync(CourseExamLaunch launch)");
        Assert.Contains("_lastCourseLessonLaunch = launch;", openLesson, StringComparison.Ordinal);
        Assert.Contains("await _courseLesson.PrepareAsync(launch, _lifetime.Token);", openLesson, StringComparison.Ordinal);
        Assert.Contains("SetSelection(\"path\");", openLesson, StringComparison.Ordinal);

        var openExam = Slice(
            shellSource,
            "private async Task OpenCourseExamAsync(CourseExamLaunch launch)",
            "private async Task OpenTextPracticeAsync(string? level)");
        Assert.Contains("_lastCourseExamLaunch = launch;", openExam, StringComparison.Ordinal);
        Assert.Contains("await _courseLesson.PrepareExamAsync(launch, _lifetime.Token);", openExam, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenLevelAsync", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenLevelModuleAsync", shellSource, StringComparison.Ordinal);
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

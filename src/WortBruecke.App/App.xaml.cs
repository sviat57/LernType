using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WortBruecke.App.Infrastructure;
using WortBruecke.App.ViewModels;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Audio;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Dictionary;
using WortBruecke.Infrastructure.Images;
using WortBruecke.Infrastructure.Keyboard;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;
using WortBruecke.Infrastructure.Settings;

namespace WortBruecke.App;

public partial class App : Application
{
    private IHost? _host;
    private MainViewModel? _viewModel;
    private SqliteDatabase? _database;
    private IManagedBackupService? _managedBackups;
    private LocalDiagnosticsService? _diagnostics;
    private int _startupInProgress;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            _host = BuildHost();
            _diagnostics = _host.Services.GetRequiredService<LocalDiagnosticsService>();
            _database = _host.Services.GetRequiredService<SqliteDatabase>();
            _managedBackups = _host.Services.GetRequiredService<IManagedBackupService>();
            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
            _viewModel.StartupRetryRequested += InitializeStorageAsync;

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            await _host.StartAsync();
            QueueTemporaryAudioCleanup();
            try
            {
                var startupSettings = await _host.Services.GetRequiredService<ISettingsStore>().LoadAsync();
                ThemeManager.Apply(startupSettings.UseDarkTheme);
            }
            catch (Exception settingsException) when (settingsException is IOException or UnauthorizedAccessException)
            {
                // The shell still opens with safe theme defaults; Settings exposes the retry path.
                _diagnostics.Write("startup.settings.failed", settingsException);
                ThemeManager.Apply(false);
            }
            await InitializeStorageAsync(CancellationToken.None);
            await Dispatcher.InvokeAsync(
                ShowKeyboardLayoutPrerequisiteIfNeeded,
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception exception)
        {
            _diagnostics?.Write("startup.composition.failed", exception);
            if (_viewModel is not null && MainWindow is not null)
            {
                _viewModel.ReportStartupFailure(exception);
                return;
            }
            MessageBox.Show(
                "LernType не запустился из-за ошибки конфигурации. Переустановите приложение или обратитесь в поддержку.",
                "LernType",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Services.AddSingleton<AppPaths>();
        builder.Services.AddSingleton<LocalDiagnosticsService>();
        builder.Services.AddSingleton<JsonContentLoader>();
        builder.Services.AddSingleton<SqliteDatabase>();
        builder.Services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        builder.Services.AddSingleton<IKeyboardLayoutService, WindowsKeyboardLayoutService>();
        builder.Services.AddSingleton<IContentRepository, SqliteContentRepository>();
        builder.Services.AddSingleton<ICourseCatalogRepository>(services =>
            new JsonCourseCatalogRepository(services.GetRequiredService<AppPaths>().ContentRoot));
        builder.Services.AddSingleton<ICourseProgressRepository, SqliteCourseProgressRepository>();
        builder.Services.AddSingleton<IAttemptRepository, SqliteAttemptRepository>();
        builder.Services.AddSingleton<IReviewStateRepository, SqliteReviewStateRepository>();
        builder.Services.AddSingleton<IManagedBackupService, ManagedBackupService>();
        builder.Services.AddSingleton<IOfflineDictionaryService>(_ =>
        {
            var dictionaryPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Dictionary",
                "FreeDict",
                "freedict-ru-de-2025.11.23.sqlite");
            return new FreeDictOfflineDictionaryService(dictionaryPath);
        });
        builder.Services.AddSingleton<IImageProvider>(_ => new LocalImageProvider(AppContext.BaseDirectory));
        builder.Services.AddSingleton<IAudioPracticeService, WindowsAudioPracticeService>();
        builder.Services.AddSingleton<TemporaryAudioRecordingStore>();
        builder.Services.AddSingleton<MainViewModel>(services => new MainViewModel(
            services.GetRequiredService<IContentRepository>(),
            services.GetRequiredService<IKeyboardLayoutService>(),
            services.GetRequiredService<IImageProvider>(),
            services.GetRequiredService<ISettingsStore>(),
            services.GetRequiredService<ICourseCatalogRepository>(),
            services.GetRequiredService<ICourseProgressRepository>(),
            services.GetRequiredService<IAttemptRepository>(),
            services.GetRequiredService<IReviewStateRepository>(),
            services.GetRequiredService<IAudioPracticeService>(),
            services.GetRequiredService<TemporaryAudioRecordingStore>()));
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    private void QueueTemporaryAudioCleanup()
    {
        if (_host is null)
        {
            return;
        }
        var recordingStore = _host.Services.GetRequiredService<TemporaryAudioRecordingStore>();
        _ = ObserveTemporaryAudioCleanupAsync(recordingStore);
    }

    private async Task ObserveTemporaryAudioCleanupAsync(TemporaryAudioRecordingStore recordingStore)
    {
        try
        {
            await recordingStore.CleanupOrphansAsync();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException))
        {
            _diagnostics?.Write("startup.audio-temp-cleanup.failed", exception);
        }
    }

    private void ShowKeyboardLayoutPrerequisiteIfNeeded()
    {
        if (_host is null || MainWindow is null)
        {
            return;
        }

        try
        {
            var pair = LanguagePair.RussianToGerman;
            var layoutService = _host.Services.GetRequiredService<IKeyboardLayoutService>();
            var hasMissingLayout = layoutService
                .CheckInstalled(pair.Source.CultureCode, pair.Target.CultureCode)
                .Any(layout => !layout.IsInstalled);
            if (!hasMissingLayout)
            {
                return;
            }

            var setupWindow = new LayoutSetupWindow(layoutService, pair)
            {
                Owner = MainWindow
            };
            _ = setupWindow.ShowDialog();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException))
        {
            // Layout setup is a recoverable prerequisite; Settings keeps the same retry path.
            _diagnostics?.Write("startup.keyboard-layout-check.failed", exception);
        }
    }

    private async Task InitializeStorageAsync(CancellationToken cancellationToken)
    {
        if (_database is null || _viewModel is null ||
            Interlocked.CompareExchange(ref _startupInProgress, 1, 0) != 0)
        {
            return;
        }
        try
        {
            try
            {
                await _database.InitializeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _diagnostics?.Write("startup.storage.failed", exception);
                _viewModel.ReportStartupFailure(exception);
                return;
            }

            _viewModel.MarkStorageReady();
            try
            {
                await _viewModel.InitializeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception settingsException)
            {
                // SQLite is healthy; a settings failure degrades only settings/theme state.
                _diagnostics?.Write("startup.settings.initialize.failed", settingsException);
                _viewModel.ReportUnhandledFailure(settingsException);
            }

            if (_managedBackups is not null)
            {
                try
                {
                    await _managedBackups.CreateRollingBackupAsync(cancellationToken);
                    await _managedBackups.ApplyRetentionAsync(cancellationToken);
                }
                catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
                {
                    _diagnostics?.Write("backup.refresh.failed", backupException);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _startupInProgress, 0);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _diagnostics?.Write("dispatcher.unhandled", e.Exception);
        if (e.Exception is OutOfMemoryException or AccessViolationException)
        {
            return;
        }
        _viewModel?.ReportUnhandledFailure(e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        if (_viewModel is not null) _viewModel.StartupRetryRequested -= InitializeStorageAsync;
        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (_host is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    _host.Dispose();
                }
            }
            catch (Exception exception)
            {
                _diagnostics?.Write("shutdown.failed", exception);
            }
        }
        base.OnExit(e);
    }
}

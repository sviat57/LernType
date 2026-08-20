using System.Net.Http;
using System.IO;
using System.Windows;
using WortBruecke.App.Infrastructure;
using WortBruecke.App.ViewModels;
using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Analysis;
using WortBruecke.Infrastructure.Dictionary;
using WortBruecke.Infrastructure.Images;
using WortBruecke.Infrastructure.Keyboard;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;
using WortBruecke.Infrastructure.Settings;

namespace WortBruecke.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var keyboardLayoutService = new WindowsKeyboardLayoutService();
            var pair = WortBruecke.Core.Models.LanguagePair.RussianToGerman;
            var availability = keyboardLayoutService.CheckInstalled(pair.Source.CultureCode, pair.Target.CultureCode);
            if (availability.Any(item => !item.IsInstalled))
            {
                var setupWindow = new LayoutSetupWindow(keyboardLayoutService, pair);
                if (setupWindow.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }

            var paths = new AppPaths();
            var database = new SqliteDatabase(paths, new JsonContentLoader());
            await database.InitializeAsync();
            var settingsStore = new JsonSettingsStore(paths);
            var startupSettings = await settingsStore.LoadAsync();
            ThemeManager.Apply(startupSettings.UseDarkTheme);
            var languageAnalysisService = new OpenAiLanguageAnalysisService(
                new HttpClient { Timeout = TimeSpan.FromSeconds(90) },
                settingsStore);
            var dictionaryPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Dictionary",
                "FreeDict",
                "freedict-ru-de-2025.11.23.sqlite");
            var offlineDictionary = new FreeDictOfflineDictionaryService(dictionaryPath);
            var bookRepository = new SqliteBookRepository(database);
            var bookExtractor = new WortBruecke.Core.Training.BookVocabularyExtractor(offlineDictionary);

            var viewModel = new MainViewModel(
                new SqliteContentRepository(database),
                new SqliteProgressRepository(database),
                keyboardLayoutService,
                new LocalImageProvider(AppContext.BaseDirectory),
                languageAnalysisService,
                settingsStore,
                bookRepository,
                bookExtractor,
                offlineDictionary,
                new SqliteLearningProgressRepository(database),
                new JsonExamBlueprintRepository(paths.ContentRoot));
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"LernType не удалось запустить.\n\n{exception.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

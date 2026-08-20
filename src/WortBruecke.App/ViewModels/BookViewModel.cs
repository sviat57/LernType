using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.App.ViewModels;

public sealed record BookLanguageOption(string CultureCode, string TargetCultureCode, string Title, string Description);

public sealed class BookWordViewModel : ObservableObject
{
    private bool _isSelected = true;

    public BookWordViewModel(ExtractedVocabularyItem item) => Item = item;

    public ExtractedVocabularyItem Item { get; }
    public string Source => Item.Source;
    public string Translation => string.Join(" / ", Item.Translations.Take(3));
    public string Meta => $"{Item.Frequency}×{(string.IsNullOrWhiteSpace(Item.PartOfSpeech) ? string.Empty : $" · {Item.PartOfSpeech}")}";
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class BookViewModel : ObservableObject
{
    private readonly IBookRepository _bookRepository;
    private readonly IBookVocabularyExtractor _extractor;
    private readonly IProgressRepository _progressRepository;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly string _dictionaryAttribution;
    private readonly List<BookWordViewModel> _practiceWords = [];
    private BookLanguageOption? _selectedLanguage;
    private BookLanguageOption? _vocabularyLanguage;
    private string _title = string.Empty;
    private string _bookText = string.Empty;
    private string _statusMessage = "Вставьте отрывок — обработка и словарь работают полностью локально.";
    private string _answer = string.Empty;
    private bool _isAnalyzing;
    private bool _isPractising;
    private bool _isComplete;
    private bool _showFeedback;
    private bool _isCorrect;
    private int _currentIndex;
    private int _correctCount;

    public BookViewModel(
        IBookRepository bookRepository,
        IBookVocabularyExtractor extractor,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IOfflineDictionaryService dictionary)
    {
        _bookRepository = bookRepository;
        _extractor = extractor;
        _progressRepository = progressRepository;
        _keyboardLayoutService = keyboardLayoutService;
        _dictionaryAttribution = dictionary.Attribution;

        Languages =
        [
            new BookLanguageOption("de-DE", "ru-RU", "Немецкий текст", "Слова нужно переводить на русский"),
            new BookLanguageOption("ru-RU", "de-DE", "Русский текст", "Слова нужно переводить на немецкий")
        ];
        SelectedLanguage = Languages[0];

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanAnalyze);
        StartPracticeCommand = new RelayCommand(StartPractice, CanStartPractice);
        CheckCommand = new AsyncRelayCommand(CheckAsync, CanCheck);
        NextCommand = new RelayCommand(Next, () => ShowFeedback);
        BackToBookCommand = new RelayCommand(BackToBook);
        NewBookCommand = new RelayCommand(NewBook, () => !IsAnalyzing);
        SelectRecentCommand = new ParameterizedRelayCommand(SelectRecent, parameter => !IsAnalyzing && parameter is UserBook);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public ObservableCollection<BookLanguageOption> Languages { get; }
    public ObservableCollection<BookWordViewModel> Words { get; } = [];
    public ObservableCollection<UserBook> RecentBooks { get; } = [];
    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand StartPracticeCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand BackToBookCommand { get; }
    public RelayCommand NewBookCommand { get; }
    public ParameterizedRelayCommand SelectRecentCommand { get; }
    public ParameterizedRelayCommand InsertGermanCharacterCommand { get; }

    public BookLanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                if (!IsAnalyzing && Words.Count > 0 &&
                    !string.Equals(_vocabularyLanguage?.CultureCode, value?.CultureCode, StringComparison.OrdinalIgnoreCase))
                {
                    ClearVocabulary("Язык текста изменён. Извлеките слова заново.");
                }
                AnalyzeCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowGermanCharacters));
                OnPropertyChanged(nameof(PromptInstruction));
                OnPropertyChanged(nameof(InputLabel));
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BookText
    {
        get => _bookText;
        set
        {
            if (SetProperty(ref _bookText, value))
            {
                if (!IsAnalyzing && Words.Count > 0)
                {
                    ClearVocabulary("Текст изменён. Извлеките слова заново, чтобы обновить тренировку.");
                }
                AnalyzeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CharacterCount));
            }
        }
    }

    public string Answer
    {
        get => _answer;
        set
        {
            if (SetProperty(ref _answer, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(IsNotAnalyzing));
                AnalyzeCommand.RaiseCanExecuteChanged();
                StartPracticeCommand.RaiseCanExecuteChanged();
                NewBookCommand.RaiseCanExecuteChanged();
                SelectRecentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPractising
    {
        get => _isPractising;
        private set
        {
            if (SetProperty(ref _isPractising, value))
            {
                OnPropertyChanged(nameof(IsEditorVisible));
            }
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            if (SetProperty(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(IsEditorVisible));
            }
        }
    }

    public bool ShowFeedback
    {
        get => _showFeedback;
        private set
        {
            if (SetProperty(ref _showFeedback, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCorrect
    {
        get => _isCorrect;
        private set => SetProperty(ref _isCorrect, value);
    }

    public bool IsNotAnalyzing => !IsAnalyzing;
    public bool IsEditorVisible => !IsPractising && !IsComplete;
    public bool HasVocabulary => Words.Count > 0;
    public bool HasRecentBooks => RecentBooks.Count > 0;
    public bool ShowGermanCharacters => ActiveLanguage?.TargetCultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true;
    public string CharacterCount => $"{BookText.Length:N0} / {BookVocabularyExtractor.MaximumTextLength:N0} знаков";
    public string DictionaryNote => $"Источник словаря: {_dictionaryAttribution}";
    public string PromptInstruction => ActiveLanguage?.CultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true
        ? "ПЕРЕВЕДИТЕ С НЕМЕЦКОГО" : "ПЕРЕВЕДИТЕ С РУССКОГО";
    public string Prompt => CurrentWord?.Item.Source ?? string.Empty;
    public string Context => CurrentWord?.Item.Context ?? string.Empty;
    public string InputLabel => ActiveLanguage?.TargetCultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true
        ? "Перевод по-немецки" : "Перевод по-русски";
    public string ProgressText => _practiceWords.Count == 0 ? "0 / 0" : $"{Math.Min(_currentIndex + 1, _practiceWords.Count)} / {_practiceWords.Count}";
    public double ProgressValue => _practiceWords.Count == 0 ? 0 : (double)Math.Min(_currentIndex + 1, _practiceWords.Count) / _practiceWords.Count * 100;
    public string FeedbackTitle => IsCorrect ? "Верно" : "Другой вариант";
    public string FeedbackDetail => IsCorrect ? "Перевод найден среди словарных вариантов." : $"Принятые варианты: {AcceptedAnswers}";
    public string CompletionTitle => $"{_correctCount} из {_practiceWords.Count} верно";
    public string CompletionDetail => _practiceWords.Count == 0 ? string.Empty : $"Точность: {(double)_correctCount / _practiceWords.Count:P0}. Можно изменить выбранные слова и повторить.";

    private BookWordViewModel? CurrentWord => _practiceWords.ElementAtOrDefault(_currentIndex);
    private BookLanguageOption? ActiveLanguage => _vocabularyLanguage ?? SelectedLanguage;
    private string AcceptedAnswers => CurrentWord is null ? string.Empty : string.Join(" / ", CurrentWord.Item.Translations.Take(6));

    public async Task InitializeAsync() => await RefreshRecentAsync();

    private bool CanAnalyze() => !IsAnalyzing && SelectedLanguage is not null;

    private async Task AnalyzeAsync()
    {
        if (SelectedLanguage is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(BookText))
        {
            StatusMessage = "Добавьте название и текст книги — оба поля обязательны.";
            return;
        }

        var language = SelectedLanguage;
        var title = Title.Trim();
        var text = BookText;

        IsAnalyzing = true;
        StatusMessage = "Извлекаем частотные слова и сверяем их с локальным словарём…";
        Words.Clear();
        _vocabularyLanguage = null;
        NotifyVocabularyChanged();
        try
        {
            var result = await Task.Run(async () => await _extractor.ExtractAsync(text, language.CultureCode, 50));

            if (result.Items.Count > 0)
            {
                var savedBook = await _bookRepository.SaveAsync(title, language.CultureCode, text, result.Items);
                Title = savedBook.Title;
                BookText = savedBook.RawText;
                SelectedLanguage = language;
                _vocabularyLanguage = language;
                foreach (var item in savedBook.Vocabulary)
                {
                    AddWord(item);
                }
                StatusMessage = $"Найдено {Words.Count} слов из {result.UniqueWordCount} уникальных форм. Не распознано или отброшено: {result.UnresolvedWordCount}.";
                await RefreshRecentAsync();
            }
            else
            {
                StatusMessage = "Словарных совпадений не найдено. Проверьте язык текста или попробуйте другой отрывок.";
            }
        }
        catch (ArgumentException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Не удалось обработать текст: {exception.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            NotifyVocabularyChanged();
        }
    }

    private bool CanStartPractice() => !IsAnalyzing && Words.Any(word => word.IsSelected);

    private void StartPractice()
    {
        _practiceWords.Clear();
        _practiceWords.AddRange(Words.Where(word => word.IsSelected).OrderBy(_ => Random.Shared.Next()).Take(30));
        if (_practiceWords.Count == 0)
        {
            return;
        }
        _currentIndex = 0;
        _correctCount = 0;
        IsComplete = false;
        IsPractising = true;
        LoadQuestion();
    }

    private bool CanCheck() => IsPractising && !ShowFeedback && !string.IsNullOrWhiteSpace(Answer);

    private async Task CheckAsync()
    {
        if (CurrentWord is null || ActiveLanguage is null)
        {
            return;
        }
        IsCorrect = AnswerEvaluator.Evaluate(Answer, CurrentWord.Item.Translations, ActiveLanguage.TargetCultureCode).IsCorrect;
        if (IsCorrect)
        {
            _correctCount++;
        }
        ShowFeedback = true;
        if (CurrentWord.Item.Id <= 0)
        {
            throw new InvalidOperationException("Слово книги не было сохранено в локальной базе.");
        }
        await _progressRepository.RecordAttemptAsync(ContentType.BookWord, CurrentWord.Item.Id, IsCorrect);
        OnPropertyChanged(nameof(FeedbackTitle));
        OnPropertyChanged(nameof(FeedbackDetail));
    }

    private void Next()
    {
        _currentIndex++;
        if (_currentIndex >= _practiceWords.Count)
        {
            IsPractising = false;
            IsComplete = true;
            ShowFeedback = false;
            OnPropertyChanged(nameof(CompletionTitle));
            OnPropertyChanged(nameof(CompletionDetail));
            return;
        }
        LoadQuestion();
    }

    private void LoadQuestion()
    {
        Answer = string.Empty;
        ShowFeedback = false;
        if (ActiveLanguage is not null)
        {
            _keyboardLayoutService.SwitchTo(ActiveLanguage.TargetCultureCode);
        }
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(PromptInstruction));
        OnPropertyChanged(nameof(InputLabel));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private void BackToBook()
    {
        IsPractising = false;
        IsComplete = false;
        ShowFeedback = false;
        Answer = string.Empty;
    }

    private void NewBook()
    {
        BackToBook();
        Words.Clear();
        _practiceWords.Clear();
        _vocabularyLanguage = null;
        Title = string.Empty;
        BookText = string.Empty;
        StatusMessage = "Вставьте новый отрывок — ничего не отправляется в интернет.";
        NotifyVocabularyChanged();
    }

    private void SelectRecent(object? parameter)
    {
        if (parameter is not UserBook book)
        {
            return;
        }
        BackToBook();
        Words.Clear();
        _practiceWords.Clear();
        _vocabularyLanguage = null;
        Title = book.Title;
        BookText = book.RawText;
        SelectedLanguage = Languages.FirstOrDefault(item => item.CultureCode == book.SourceCulture) ?? Languages[0];
        _vocabularyLanguage = SelectedLanguage;
        foreach (var item in book.Vocabulary)
        {
            AddWord(item);
        }
        StatusMessage = $"Загружено из локальной библиотеки: {book.Vocabulary.Count} слов.";
        NotifyVocabularyChanged();
    }

    private async Task RefreshRecentAsync()
    {
        RecentBooks.Clear();
        foreach (var book in await _bookRepository.GetRecentAsync(6))
        {
            RecentBooks.Add(book);
        }
        OnPropertyChanged(nameof(HasRecentBooks));
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character)
        {
            Answer += character;
        }
    }

    private void NotifyVocabularyChanged()
    {
        OnPropertyChanged(nameof(HasVocabulary));
        StartPracticeCommand.RaiseCanExecuteChanged();
    }

    private void ClearVocabulary(string message)
    {
        Words.Clear();
        _practiceWords.Clear();
        _vocabularyLanguage = null;
        StatusMessage = message;
        NotifyVocabularyChanged();
        OnPropertyChanged(nameof(ShowGermanCharacters));
        OnPropertyChanged(nameof(PromptInstruction));
        OnPropertyChanged(nameof(InputLabel));
    }

    private void AddWord(ExtractedVocabularyItem item)
    {
        var row = new BookWordViewModel(item);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BookWordViewModel.IsSelected))
            {
                StartPracticeCommand.RaiseCanExecuteChanged();
            }
        };
        Words.Add(row);
    }
}

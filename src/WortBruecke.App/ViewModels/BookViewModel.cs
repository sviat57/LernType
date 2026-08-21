using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;
using WortBruecke.Infrastructure.Persistence;

namespace WortBruecke.App.ViewModels;

public sealed record BookLanguageOption(string CultureCode, string TargetCultureCode, string Title, string Description);

public enum BookOperationState
{
    Idle,
    Analyzing,
    DraftReady,
    Saving,
    Saved,
    Loading,
    Deleting,
    Exporting,
    Canceled,
    Error
}

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
    private readonly LearningAttemptSink _attemptSink;
    private readonly IKeyboardLayoutService _keyboardLayoutService;
    private readonly string _dictionaryAttribution;
    private readonly List<BookWordViewModel> _practiceWords = [];
    private BookLanguageOption? _selectedLanguage;
    private BookLanguageOption? _vocabularyLanguage;
    private UserBookSummary? _pendingDeletion;
    private string _title = string.Empty;
    private string _bookText = string.Empty;
    private string _statusMessage = "Вставьте отрывок — он остаётся временным, пока вы явно не сохраните его.";
    private string _answer = string.Empty;
    private bool _isAnalyzing;
    private bool _isPractising;
    private bool _isComplete;
    private bool _showFeedback;
    private bool _isCorrect;
    private bool _isSaved;
    private bool _isLoadingBook;
    private bool _pendingDeleteAll;
    private long? _currentBookId;
    private BookOperationState _operationState;
    private OperationError? _operationError;
    private int _currentIndex;
    private int _correctCount;
    private Guid _practiceSessionId;
    private DateTimeOffset _attemptStartedAtUtc;

    public BookViewModel(
        IBookRepository bookRepository,
        IBookVocabularyExtractor extractor,
        IProgressRepository progressRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IOfflineDictionaryService dictionary)
        : this(bookRepository, extractor, new LearningAttemptSink(progressRepository), keyboardLayoutService, dictionary)
    {
    }

    public BookViewModel(
        IBookRepository bookRepository,
        IBookVocabularyExtractor extractor,
        IAttemptRepository attemptRepository,
        IKeyboardLayoutService keyboardLayoutService,
        IOfflineDictionaryService dictionary)
        : this(bookRepository, extractor, new LearningAttemptSink(attemptRepository), keyboardLayoutService, dictionary)
    {
    }

    private BookViewModel(
        IBookRepository bookRepository,
        IBookVocabularyExtractor extractor,
        LearningAttemptSink attemptSink,
        IKeyboardLayoutService keyboardLayoutService,
        IOfflineDictionaryService dictionary)
    {
        _bookRepository = bookRepository;
        _extractor = extractor;
        _attemptSink = attemptSink;
        _keyboardLayoutService = keyboardLayoutService;
        _dictionaryAttribution = dictionary.Attribution;

        Languages =
        [
            new BookLanguageOption("de-DE", "ru-RU", "Немецкий текст", "Слова нужно переводить на русский"),
            new BookLanguageOption("ru-RU", "de-DE", "Русский текст", "Слова нужно переводить на немецкий")
        ];
        SelectedLanguage = Languages[0];

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanAnalyze, HandleCommandError);
        SaveBookCommand = new AsyncRelayCommand(SaveBookAsync, CanSaveBook, HandleCommandError);
        CancelOperationCommand = new RelayCommand(CancelPendingOperations, () => IsAnalyzing || IsBusy);
        StartPracticeCommand = new RelayCommand(StartPractice, CanStartPractice);
        CheckCommand = new AsyncRelayCommand(CheckAsync, CanCheck, HandleCommandError);
        NextCommand = new RelayCommand(Next, () => ShowFeedback);
        BackToBookCommand = new RelayCommand(BackToBook);
        NewBookCommand = new RelayCommand(NewBook, () => !IsBusy);
        SelectRecentCommand = new AsyncParameterizedRelayCommand(SelectRecentAsync, parameter => !IsBusy && parameter is UserBookSummary, HandleCommandError);
        RequestDeleteCommand = new ParameterizedRelayCommand(RequestDelete, parameter => !IsBusy && parameter is UserBookSummary);
        RequestDeleteAllCommand = new RelayCommand(RequestDeleteAll, () => !IsBusy && HasRecentBooks);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => !IsBusy && HasPendingDeletion, HandleCommandError);
        CancelDeleteCommand = new RelayCommand(CancelDeletion, () => HasPendingDeletion);
        ExportBookCommand = new AsyncRelayCommand(ExportBookAsync, () => !IsBusy && IsSaved, HandleCommandError);
        InsertGermanCharacterCommand = new ParameterizedRelayCommand(InsertGermanCharacter, parameter => parameter is string);
    }

    public ObservableCollection<BookLanguageOption> Languages { get; }
    public ObservableCollection<BookWordViewModel> Words { get; } = [];
    public ObservableCollection<UserBookSummary> RecentBooks { get; } = [];
    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand SaveBookCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand StartPracticeCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand BackToBookCommand { get; }
    public RelayCommand NewBookCommand { get; }
    public AsyncParameterizedRelayCommand SelectRecentCommand { get; }
    public ParameterizedRelayCommand RequestDeleteCommand { get; }
    public RelayCommand RequestDeleteAllCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public AsyncRelayCommand ExportBookCommand { get; }
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
                RefreshCommandStates();
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
                if (!_isLoadingBook && IsSaved)
                {
                    DetachSavedIdentityPreservingVocabulary();
                }
                RefreshCommandStates();
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
                if (!_isLoadingBook && !IsAnalyzing && Words.Count > 0)
                {
                    ClearVocabulary("Текст изменён. Извлеките слова заново, чтобы обновить тренировку.");
                }
                RefreshCommandStates();
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

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public OperationError? OperationError { get => _operationError; private set { if (SetProperty(ref _operationError, value)) OnPropertyChanged(nameof(HasOperationError)); } }
    public bool HasOperationError => OperationError is not null;
    public BookOperationState OperationState
    {
        get => _operationState;
        private set
        {
            var wasBusy = IsBusy;
            if (SetProperty(ref _operationState, value))
            {
                if (wasBusy != IsBusy) OnPropertyChanged(nameof(IsBusy));
                RefreshCommandStates();
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(IsNotAnalyzing));
                OnPropertyChanged(nameof(IsBusy));
                RefreshCommandStates();
            }
        }
    }

    public bool IsBusy => OperationState is BookOperationState.Analyzing or BookOperationState.Saving or
        BookOperationState.Loading or BookOperationState.Deleting or BookOperationState.Exporting;

    public bool IsPractising
    {
        get => _isPractising;
        private set { if (SetProperty(ref _isPractising, value)) OnPropertyChanged(nameof(IsEditorVisible)); }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set { if (SetProperty(ref _isComplete, value)) OnPropertyChanged(nameof(IsEditorVisible)); }
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

    public bool IsCorrect { get => _isCorrect; private set => SetProperty(ref _isCorrect, value); }
    public bool IsSaved
    {
        get => _isSaved;
        private set
        {
            if (SetProperty(ref _isSaved, value))
            {
                OnPropertyChanged(nameof(IsDraft));
                ExportBookCommand.RaiseCanExecuteChanged();
                SaveBookCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public UserBookSummary? PendingDeletion
    {
        get => _pendingDeletion;
        private set
        {
            if (SetProperty(ref _pendingDeletion, value))
            {
                OnPropertyChanged(nameof(HasPendingDeletion));
                OnPropertyChanged(nameof(DeletionPrompt));
                ConfirmDeleteCommand.RaiseCanExecuteChanged();
                CancelDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDraft => HasVocabulary && !IsSaved;
    public bool IsNotAnalyzing => !IsAnalyzing;
    public bool IsEditorVisible => !IsPractising && !IsComplete;
    public bool HasVocabulary => Words.Count > 0;
    public bool HasRecentBooks => RecentBooks.Count > 0;
    public bool HasPendingDeletion => _pendingDeleteAll || PendingDeletion is not null;
    public bool ShowGermanCharacters => ActiveLanguage?.TargetCultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true;
    public string CharacterCount => $"{BookText.Length:N0} / {BookVocabularyExtractor.MaximumTextLength:N0} знаков";
    public string DictionaryNote => $"Источник словаря: {_dictionaryAttribution}";
    public string ProvenanceNote => _vocabularyLanguage is null
        ? "Язык ещё не зафиксирован"
        : $"Направление извлечения: {_vocabularyLanguage.CultureCode} → {_vocabularyLanguage.TargetCultureCode}";
    public string DraftPrivacyNote => IsSaved
        ? "Текст сохранён в локальной библиотеке. Его можно экспортировать или удалить."
        : "Черновик хранится только в текущей сессии и исчезнет после закрытия приложения.";
    public string DeletionPrompt => _pendingDeleteAll
        ? "Удалить все сохранённые книги, их слова и связанную статистику?"
        : PendingDeletion is null ? string.Empty : $"Удалить «{PendingDeletion.Title}» и связанную статистику?";
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RefreshRecentAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OperationState = BookOperationState.Canceled;
        }
        catch (Exception exception)
        {
            SetOperationError(exception, "Не удалось загрузить список локальных книг. Новый временный текст всё ещё доступен.");
        }
    }

    public void CancelPendingOperations()
    {
        AnalyzeCommand.Cancel();
        SaveBookCommand.Cancel();
        CheckCommand.Cancel();
        SelectRecentCommand.Cancel();
        ConfirmDeleteCommand.Cancel();
        ExportBookCommand.Cancel();
        if (IsBusy)
        {
            StatusMessage = "Отменяем операцию…";
        }
    }

    private bool CanAnalyze() => !IsBusy && SelectedLanguage is not null &&
        !string.IsNullOrWhiteSpace(BookText) && BookText.Length <= BookVocabularyExtractor.MaximumTextLength;

    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        if (SelectedLanguage is null || string.IsNullOrWhiteSpace(BookText))
        {
            SetValidationError("Добавьте текст и выберите его язык.");
            return;
        }

        var language = SelectedLanguage;
        var text = BookText;
        IsAnalyzing = true;
        OperationState = BookOperationState.Analyzing;
        OperationError = null;
        StatusMessage = "Извлекаем частотные слова и сверяем их с локальным словарём…";
        Words.Clear();
        _vocabularyLanguage = null;
        SetSavedIdentity(null);
        NotifyVocabularyChanged();
        try
        {
            var result = await Task.Run(
                () => _extractor.ExtractAsync(text, language.CultureCode, 50, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Items.Count == 0)
            {
                OperationState = BookOperationState.Idle;
                StatusMessage = "Словарных совпадений не найдено. Проверьте язык текста или попробуйте другой отрывок.";
                return;
            }

            _vocabularyLanguage = language;
            foreach (var item in result.Items)
            {
                AddWord(item with { Id = 0 });
            }
            OperationState = BookOperationState.DraftReady;
            StatusMessage = $"Найдено {Words.Count} слов из {result.UniqueWordCount} уникальных форм. Черновик ещё не сохранён.";
            NotifyVocabularyChanged();
        }
        catch (OperationCanceledException)
        {
            OperationState = BookOperationState.Canceled;
            StatusMessage = "Локальный анализ отменён; текст не сохранялся.";
        }
        catch (ArgumentException)
        {
            SetValidationError("Проверьте язык и размер текста, затем повторите локальный анализ.");
        }
        catch (Exception exception)
        {
            SetOperationError(exception, "Не удалось обработать текст. Исходный текст не сохранялся.");
        }
        finally
        {
            IsAnalyzing = false;
            NotifyVocabularyChanged();
        }
    }

    private bool CanSaveBook() => !IsBusy && HasVocabulary && !IsSaved && !string.IsNullOrWhiteSpace(Title) &&
        !string.IsNullOrWhiteSpace(BookText);

    private async Task SaveBookAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveBook() || _vocabularyLanguage is null)
        {
            SetValidationError("Добавьте название и сначала извлеките слова из текста.");
            return;
        }
        OperationState = BookOperationState.Saving;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommandStates();
        OperationError = null;
        var saveCommitted = false;
        try
        {
            var savedBook = await _bookRepository.SaveAsync(
                Title.Trim(),
                _vocabularyLanguage.CultureCode,
                BookText,
                Words.Select(word => word.Item).ToArray(),
                cancellationToken);
            saveCommitted = true;
            LoadBook(savedBook);
            OperationState = BookOperationState.Saved;
            StatusMessage = "Книга явно сохранена только в локальной библиотеке.";
            await RefreshRecentAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            OperationState = saveCommitted ? BookOperationState.Saved : BookOperationState.Canceled;
            StatusMessage = saveCommitted
                ? "Книга сохранена; обновление списка недавних книг отменено."
                : "Сохранение отменено.";
        }
        catch (Exception exception)
        {
            SetOperationError(exception, IsSaved
                ? "Книга сохранена, но список недавних книг пока не обновился."
                : "Не удалось сохранить книгу. Черновик остаётся в текущей сессии.");
        }
        finally
        {
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommandStates();
        }
    }

    private bool CanStartPractice() => !IsBusy && Words.Any(word => word.IsSelected);

    private void StartPractice()
    {
        _practiceWords.Clear();
        _practiceWords.AddRange(Words.Where(word => word.IsSelected).OrderBy(_ => Random.Shared.Next()).Take(30));
        if (_practiceWords.Count == 0) return;
        _currentIndex = 0;
        _correctCount = 0;
        _practiceSessionId = Guid.NewGuid();
        IsComplete = false;
        IsPractising = true;
        LoadQuestion();
    }

    private bool CanCheck() => IsPractising && !ShowFeedback && !string.IsNullOrWhiteSpace(Answer);

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (CurrentWord is null || ActiveLanguage is null) return;
        IsCorrect = AnswerEvaluator.Evaluate(Answer, CurrentWord.Item.Translations, ActiveLanguage.TargetCultureCode).IsCorrect;
        if (IsCorrect) _correctCount++;
        ShowFeedback = true;
        if (CurrentWord.Item.Id > 0 && _currentBookId is > 0 && _practiceSessionId != Guid.Empty)
        {
            var direction = ActiveLanguage.TargetCultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? AttemptDirection.RussianToGerman
                : AttemptDirection.GermanToRussian;
            var completedAtUtc = DateTimeOffset.UtcNow;
            var attempt = new AttemptEvent(
                Guid.NewGuid(),
                LearningContentKey.ForBookWord(_currentBookId.Value, CurrentWord.Item.Source),
                contentRevision: 1,
                GermanLevel.A0,
                LanguageSkill.Vocabulary,
                ExerciseType.BidirectionalTranslation,
                direction,
                IsCorrect ? 1 : 0,
                AssessmentMode.Practice,
                _attemptStartedAtUtc,
                completedAtUtc,
                _practiceSessionId,
                LearningEvidenceFactory.ExactAnswerRubric,
                EvidenceQuality.Deterministic,
                objectiveId: "book.custom.vocabulary");
            await _attemptSink.RecordAsync(attempt, ContentType.BookWord, CurrentWord.Item.Id, cancellationToken);
        }
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
        if (ActiveLanguage is not null) _keyboardLayoutService.SwitchTo(ActiveLanguage.TargetCultureCode);
        _attemptStartedAtUtc = DateTimeOffset.UtcNow;
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
        _isLoadingBook = true;
        Title = string.Empty;
        BookText = string.Empty;
        _isLoadingBook = false;
        SetSavedIdentity(null);
        OperationError = null;
        OperationState = BookOperationState.Idle;
        StatusMessage = "Вставьте новый отрывок — черновик остаётся только в текущей сессии.";
        NotifyVocabularyChanged();
    }

    private async Task SelectRecentAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not UserBookSummary summary) return;
        OperationState = BookOperationState.Loading;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommandStates();
        try
        {
            var book = await _bookRepository.GetAsync(summary.Id, cancellationToken);
            if (book is null)
            {
                StatusMessage = "Книга уже удалена из локальной библиотеки.";
                await RefreshRecentAsync(cancellationToken);
                OperationState = BookOperationState.Idle;
                return;
            }
            BackToBook();
            LoadBook(book);
            OperationState = BookOperationState.Saved;
            StatusMessage = $"Загружено из локальной библиотеки: {book.Vocabulary.Count} слов.";
        }
        catch (OperationCanceledException)
        {
            OperationState = BookOperationState.Canceled;
            StatusMessage = "Загрузка книги отменена.";
        }
        catch (Exception exception)
        {
            SetOperationError(exception, "Не удалось открыть локальную книгу.");
        }
        finally
        {
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommandStates();
        }
    }

    private void LoadBook(UserBook book)
    {
        Words.Clear();
        _practiceWords.Clear();
        _vocabularyLanguage = null;
        _isLoadingBook = true;
        Title = book.Title;
        BookText = book.RawText;
        SelectedLanguage = Languages.FirstOrDefault(item => item.CultureCode == book.SourceCulture) ?? Languages[0];
        _vocabularyLanguage = SelectedLanguage;
        foreach (var item in book.Vocabulary) AddWord(item);
        _isLoadingBook = false;
        SetSavedIdentity(book.Id);
        NotifyVocabularyChanged();
    }

    private void RequestDelete(object? parameter)
    {
        if (parameter is not UserBookSummary summary) return;
        _pendingDeleteAll = false;
        PendingDeletion = summary;
    }

    private void RequestDeleteAll()
    {
        PendingDeletion = null;
        _pendingDeleteAll = true;
        NotifyDeletionChanged();
    }

    private void CancelDeletion()
    {
        _pendingDeleteAll = false;
        PendingDeletion = null;
        NotifyDeletionChanged();
    }

    private async Task ConfirmDeleteAsync(CancellationToken cancellationToken)
    {
        if (!HasPendingDeletion) return;
        var deletionCommitted = false;
        OperationState = BookOperationState.Deleting;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommandStates();
        try
        {
            if (_pendingDeleteAll)
            {
                var deleted = await _bookRepository.DeleteAllAsync(cancellationToken);
                deletionCommitted = true;
                if (IsSaved) NewBook();
                StatusMessage = $"Удалено книг: {deleted}. Связанные слова и статистика также удалены.";
            }
            else if (PendingDeletion is not null)
            {
                var id = PendingDeletion.Id;
                var deleted = await _bookRepository.DeleteAsync(id, cancellationToken);
                deletionCommitted = true;
                if (_currentBookId == id) NewBook();
                StatusMessage = deleted ? "Книга, её слова и связанная статистика удалены." : "Книга уже была удалена.";
            }
            CancelDeletion();
            await RefreshRecentAsync(cancellationToken);
            OperationState = BookOperationState.Idle;
        }
        catch (OperationCanceledException)
        {
            OperationState = deletionCommitted ? BookOperationState.Idle : BookOperationState.Canceled;
            StatusMessage = deletionCommitted
                ? "Удаление завершено; обновление списка библиотеки отменено."
                : "Удаление отменено без частичных изменений.";
        }
        catch (Exception exception)
        {
            deletionCommitted |= exception is BookPrivacyCleanupException or ManagedBackupPurgeException;
            if (deletionCommitted && IsSaved &&
                (_pendingDeleteAll || PendingDeletion?.Id == _currentBookId))
            {
                NewBook();
            }
            SetOperationError(exception, deletionCommitted
                ? "Записи удалены. Перезапустите очистку, чтобы завершить обслуживание локального хранилища."
                : "Не удалось удалить книгу; локальные данные оставлены без изменений.");
        }
        finally
        {
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommandStates();
        }
    }

    private async Task ExportBookAsync(CancellationToken cancellationToken)
    {
        if (_currentBookId is null) return;
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "Книга LernType (*.json)|*.json",
            FileName = MakeSafeFileName(Title) + ".lerntype-book.json",
            OverwritePrompt = true,
            Title = "Экспортировать локальную книгу"
        };
        if (dialog.ShowDialog() != true) return;
        var temporaryPath = $"{dialog.FileName}.{Guid.NewGuid():N}.tmp";
        OperationState = BookOperationState.Exporting;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommandStates();
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await _bookRepository.ExportAsync(_currentBookId.Value, stream, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, dialog.FileName, true);
            OperationState = BookOperationState.Saved;
            StatusMessage = "Книга экспортирована в выбранный файл.";
        }
        catch (OperationCanceledException)
        {
            if (TryDeleteIncompleteExport(temporaryPath))
            {
                OperationState = BookOperationState.Canceled;
                StatusMessage = "Экспорт отменён; неполный файл удалён.";
            }
            else
            {
                SetExportCleanupPending();
            }
        }
        catch (Exception exception)
        {
            if (TryDeleteIncompleteExport(temporaryPath))
            {
                SetOperationError(exception, "Не удалось экспортировать книгу.");
            }
            else
            {
                SetExportCleanupPending();
            }
        }
        finally
        {
            OnPropertyChanged(nameof(IsBusy));
            RefreshCommandStates();
        }
    }

    private async Task RefreshRecentAsync(CancellationToken cancellationToken)
    {
        var books = await _bookRepository.GetRecentSummariesAsync(6, cancellationToken);
        RecentBooks.Clear();
        foreach (var book in books) RecentBooks.Add(book);
        OnPropertyChanged(nameof(HasRecentBooks));
        RequestDeleteAllCommand.RaiseCanExecuteChanged();
    }

    private void InsertGermanCharacter(object? parameter)
    {
        if (parameter is string character) Answer += character;
    }

    private void SetSavedIdentity(long? bookId)
    {
        _currentBookId = bookId;
        IsSaved = bookId is > 0;
        OnPropertyChanged(nameof(DraftPrivacyNote));
    }

    private void DetachSavedIdentityPreservingVocabulary()
    {
        var items = Words.Select(word => word.Item with { Id = 0 }).ToArray();
        Words.Clear();
        foreach (var item in items) AddWord(item);
        SetSavedIdentity(null);
        OperationState = BookOperationState.DraftReady;
        StatusMessage = "Название изменено. Сохраните этот вариант явно, если хотите оставить его в библиотеке.";
        NotifyVocabularyChanged();
    }

    private void NotifyVocabularyChanged()
    {
        OnPropertyChanged(nameof(HasVocabulary));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(DraftPrivacyNote));
        OnPropertyChanged(nameof(ProvenanceNote));
        StartPracticeCommand.RaiseCanExecuteChanged();
        SaveBookCommand.RaiseCanExecuteChanged();
    }

    private void NotifyDeletionChanged()
    {
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(DeletionPrompt));
        ConfirmDeleteCommand.RaiseCanExecuteChanged();
        CancelDeleteCommand.RaiseCanExecuteChanged();
    }

    private void ClearVocabulary(string message)
    {
        Words.Clear();
        _practiceWords.Clear();
        _vocabularyLanguage = null;
        SetSavedIdentity(null);
        OperationState = BookOperationState.Idle;
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
            if (args.PropertyName == nameof(BookWordViewModel.IsSelected)) StartPracticeCommand.RaiseCanExecuteChanged();
        };
        Words.Add(row);
    }

    private void SetValidationError(string message)
    {
        OperationState = BookOperationState.Error;
        OperationError = new OperationError(OperationErrorKind.Validation, message, "BookValidation");
        StatusMessage = message;
    }

    private void SetOperationError(Exception exception, string fallbackMessage)
    {
        OperationState = BookOperationState.Error;
        OperationError = exception is BookPrivacyCleanupException or ManagedBackupPurgeException
            ? new OperationError(OperationErrorKind.StorageBusy, fallbackMessage, exception.GetType().Name)
            : WortBruecke.App.Infrastructure.OperationError.FromException(exception, fallbackMessage);
        StatusMessage = OperationError.UserMessage;
    }

    private void HandleCommandError(OperationError error)
    {
        OperationState = BookOperationState.Error;
        OperationError = error;
        StatusMessage = error.UserMessage;
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        AnalyzeCommand?.RaiseCanExecuteChanged();
        SaveBookCommand?.RaiseCanExecuteChanged();
        StartPracticeCommand?.RaiseCanExecuteChanged();
        NewBookCommand?.RaiseCanExecuteChanged();
        SelectRecentCommand?.RaiseCanExecuteChanged();
        RequestDeleteCommand?.RaiseCanExecuteChanged();
        RequestDeleteAllCommand?.RaiseCanExecuteChanged();
        ConfirmDeleteCommand?.RaiseCanExecuteChanged();
        ExportBookCommand?.RaiseCanExecuteChanged();
        CancelOperationCommand?.RaiseCanExecuteChanged();
    }

    private static string MakeSafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "book" : safe[..Math.Min(safe.Length, 80)];
    }

    private void SetExportCleanupPending()
    {
        OperationState = BookOperationState.Error;
        OperationError = new OperationError(
            OperationErrorKind.StorageBusy,
            "Неполный временный файл мог остаться рядом с выбранным файлом. Закройте использующую его программу и удалите файл с расширением .tmp.",
            "ExportCleanupPending");
        StatusMessage = OperationError.UserMessage;
    }

    private static bool TryDeleteIncompleteExport(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

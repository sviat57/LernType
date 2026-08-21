using System.Collections.ObjectModel;
using WortBruecke.App.Infrastructure;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;

namespace WortBruecke.App.ViewModels;

public sealed record SkillProgressCard(
    LanguageSkill Skill,
    string Title,
    int AttemptCount,
    int DistinctItemCount,
    double AverageScore,
    DateTimeOffset? LastAttemptUtc)
{
    public string ScoreText => AttemptCount == 0 ? "Нет evidence" : $"{AverageScore:P0}";
    public string DetailText => AttemptCount == 0
        ? "Начните с доступного задания"
        : $"{AttemptCount} попыток · {DistinctItemCount} разных заданий";
}

public sealed class ProgressViewModel : ObservableObject
{
    private readonly IAttemptRepository _attempts;
    private readonly IReviewStateRepository _reviews;
    private readonly IMasteryProjectionService _mastery;
    private readonly IClock _clock;
    private string _currentLevel = "Pre-A1";
    private string _weakestSkill = "Недостаточно данных";
    private string _errorMessage = string.Empty;
    private int _totalAttempts;
    private int _weekAttempts;
    private int _dueCount;
    private double _overallCompletion;
    private bool _isBusy;

    public ProgressViewModel(
        IAttemptRepository attempts,
        IReviewStateRepository reviews,
        IMasteryProjectionService? mastery = null,
        IClock? clock = null,
        Action<string>? navigate = null)
    {
        _attempts = attempts;
        _reviews = reviews;
        _mastery = mastery ?? new MasteryProjectionService();
        _clock = clock ?? SystemClock.Instance;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, error =>
        {
            ErrorMessage = error.UserMessage;
            OnPropertyChanged(nameof(HasError));
        });
        StartReviewCommand = new RelayCommand(() => navigate?.Invoke("trainer"), () => navigate is not null);
    }

    public ObservableCollection<SkillProgressCard> Skills { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand StartReviewCommand { get; }
    public string CurrentLevel { get => _currentLevel; private set => SetProperty(ref _currentLevel, value); }
    public string WeakestSkill { get => _weakestSkill; private set => SetProperty(ref _weakestSkill, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public int TotalAttempts { get => _totalAttempts; private set => SetProperty(ref _totalAttempts, value); }
    public int WeekAttempts { get => _weekAttempts; private set => SetProperty(ref _weekAttempts, value); }
    public int DueCount { get => _dueCount; private set => SetProperty(ref _dueCount, value); }
    public double OverallCompletion { get => _overallCompletion; private set => SetProperty(ref _overallCompletion, value); }
    public bool HasData => TotalAttempts > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Task InitializeAsync() => RefreshAsync(CancellationToken.None);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        OnPropertyChanged(nameof(HasError));
        try
        {
            var all = await _attempts.GetAsync(cancellationToken: cancellationToken);
            var due = await _reviews.GetDueAsync(_clock.UtcNow, 500, cancellationToken);
            var path = _mastery.Rebuild(GermanCurriculum.CreateDefault(), all);
            TotalAttempts = all.Count;
            WeekAttempts = all.Count(item => item.CompletedAtUtc >= _clock.UtcNow.AddDays(-7));
            DueCount = due.Count(item =>
                item.ContentKey.StartsWith("core.word.", StringComparison.Ordinal) ||
                item.ContentKey.StartsWith("core.sentence.", StringComparison.Ordinal));
            CurrentLevel = path.CurrentLevel == GermanLevel.A0 ? "Pre-A1" : path.CurrentLevel.ToString();
            OverallCompletion = path.OverallCompletion;

            var cards = Enum.GetValues<LanguageSkill>()
                .Select(skill =>
                {
                    var events = all.Where(item => item.Skill == skill)
                        .OrderByDescending(item => item.CompletedAtUtc)
                        .Take(20)
                        .ToArray();
                    return new SkillProgressCard(
                        skill,
                        SkillTitle(skill),
                        events.Length,
                        events.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).Count(),
                        events.Length == 0 ? 0 : events.Average(item => item.Score),
                        events.FirstOrDefault()?.CompletedAtUtc);
                })
                .ToArray();
            Skills.Clear();
            foreach (var card in cards)
            {
                Skills.Add(card);
            }
            WeakestSkill = cards.Where(item => item.AttemptCount > 0)
                               .OrderBy(item => item.AverageScore)
                               .ThenBy(item => item.Title, StringComparer.Ordinal)
                               .FirstOrDefault()?.Title
                           ?? "Недостаточно данных";
            OnPropertyChanged(nameof(HasData));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SkillTitle(LanguageSkill skill) => skill switch
    {
        LanguageSkill.Vocabulary => "Лексика",
        LanguageSkill.Grammar => "Грамматика",
        LanguageSkill.Reading => "Чтение",
        LanguageSkill.Listening => "Аудирование",
        LanguageSkill.Writing => "Письмо",
        LanguageSkill.Speaking => "Говорение",
        LanguageSkill.Mediation => "Медиация",
        _ => skill.ToString()
    };
}

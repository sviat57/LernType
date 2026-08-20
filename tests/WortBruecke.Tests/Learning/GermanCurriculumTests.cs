using WortBruecke.Core.Learning;

namespace WortBruecke.Tests.Learning;

public sealed class GermanCurriculumTests
{
    [Fact]
    public void DefaultPath_DefinesEveryStageFromA0ThroughC2()
    {
        var path = GermanCurriculum.CreateDefault();

        Assert.Equal(Enum.GetValues<GermanLevel>(), path.Levels.Select(level => level.Level));
        Assert.Equal(7, path.Levels.Count);
        Assert.Equal(49, path.Levels.Sum(level => level.Objectives.Count));
        Assert.Equal(49, path.Levels
            .SelectMany(level => level.Objectives)
            .Select(objective => objective.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());
    }

    [Fact]
    public void DefaultPath_CoversEverySkillAtEveryLevelAndUsesVariedExercises()
    {
        var path = GermanCurriculum.CreateDefault();
        var expectedSkills = Enum.GetValues<LanguageSkill>();

        foreach (var level in path.Levels)
        {
            Assert.Equal(expectedSkills, level.Objectives.Select(objective => objective.Skill).OrderBy(skill => skill));
            Assert.All(level.Objectives, objective =>
            {
                Assert.True(objective.IsRequired);
                Assert.False(string.IsNullOrWhiteSpace(objective.Descriptor));
            });
        }

        Assert.True(path.Levels
            .SelectMany(level => level.Objectives)
            .Select(objective => objective.ExerciseType)
            .Distinct()
            .Count() >= 15);
    }

    [Fact]
    public void DefaultPath_RaisesEvidenceStandardsAcrossAdvancedLevels()
    {
        var path = GermanCurriculum.CreateDefault();

        var thresholds = path.Levels
            .Select(level => level.Objectives.Select(objective => objective.MasteryThreshold).Distinct().Single())
            .ToArray();

        Assert.Equal([0.70, 0.72, 0.74, 0.75, 0.77, 0.80, 0.82], thresholds);
        Assert.True(path.Levels.Single(level => level.Level == GermanLevel.C1).Objectives.All(objective => objective.MinimumAttempts == 5));
        Assert.True(path.Levels.Single(level => level.Level == GermanLevel.C2).Objectives.All(objective => objective.MinimumAttempts == 5));
    }

    [Theory]
    [InlineData(GermanLevel.A0, false, null, GermanLevel.A1)]
    [InlineData(GermanLevel.A1, true, GermanLevel.A0, GermanLevel.A2)]
    [InlineData(GermanLevel.C2, true, GermanLevel.C1, null)]
    public void GermanLevel_ExposesOrderedBoundaries(
        GermanLevel level,
        bool isCefr,
        GermanLevel? previous,
        GermanLevel? next)
    {
        Assert.Equal(isCefr, level.IsCefrLevel());
        Assert.Equal(previous, level.Previous());
        Assert.Equal(next, level.Next());
    }

    [Theory]
    [InlineData("a0", GermanLevel.A0)]
    [InlineData(" B2 ", GermanLevel.B2)]
    [InlineData("c2", GermanLevel.C2)]
    public void GermanLevel_TryParse_AcceptsCanonicalCodes(string source, GermanLevel expected)
    {
        Assert.True(GermanLevelExtensions.TryParse(source, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PathDefinition_RejectsIncompleteLevelSequence()
    {
        var objective = new LearningObjective(
            "a0.vocabulary.sample",
            GermanLevel.A0,
            LanguageSkill.Vocabulary,
            ExerciseType.VocabularyRecognition,
            "Sample",
            "Sample descriptor");
        var level = new LevelDefinition(GermanLevel.A0, "A0", "Start", [objective]);

        var error = Assert.Throws<ArgumentException>(() => new LearningPathDefinition([level]));

        Assert.Contains("A0 through C2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericExam_RequiresAnOfficialCefrLevel()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            GermanCurriculum.CreateGenericFourSkillExam("pre", "Pre-course", GermanLevel.A0));

        Assert.Equal("level", error.ParamName);
    }

    [Fact]
    public void ExamDefinition_RejectsAnEvidenceWindowSmallerThanSectionMinimum()
    {
        var section = new ExamSectionDefinition(
            "reading",
            "Reading",
            [LanguageSkill.Reading],
            [ExerciseType.ReadingComprehension],
            weight: 1,
            minimumScore: 0.6,
            minimumEvidenceCount: 3);

        var error = Assert.Throws<ArgumentException>(() => new ExamDefinition(
            "sample",
            "Sample",
            GermanLevel.B1,
            [section],
            recentEvidenceWindowPerSection: 2));

        Assert.Equal("recentEvidenceWindowPerSection", error.ParamName);
    }
}

using System.Text.Json;
using WortBruecke.Core.Courses;
using WortBruecke.Core.Learning;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Courses;

public sealed class CourseCatalogTests
{
    [Fact]
    public async Task BundledCatalog_ContainsValidatedOriginalA0ToA2Route()
    {
        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content");
        var catalog = await new JsonCourseCatalogRepository(contentRoot).LoadAsync();

        Assert.Equal(1, catalog.Revision);
        Assert.Equal("german-from-zero", catalog.Track.Id);
        var published = catalog.Track.Courses.Where(course => course.Availability == CourseAvailability.Published).ToArray();
        Assert.Equal([GermanLevel.A0, GermanLevel.A1, GermanLevel.A2], published.Select(course => course.Level));
        Assert.Equal(24, published.Sum(course => course.Units.Sum(unit => unit.Lessons.Count)));
        Assert.Equal(144, published.Sum(course => course.Units.Sum(unit => unit.Lessons.Sum(lesson => lesson.Steps.Count))));
        Assert.All(published.SelectMany(course => course.Units).SelectMany(unit => unit.Lessons), lesson =>
            Assert.Equal(
                [CourseStepKind.Briefing, CourseStepKind.Writing, CourseStepKind.Reading,
                 CourseStepKind.ListeningSpeaking, CourseStepKind.Rule, CourseStepKind.Checkpoint],
                lesson.Steps.OrderBy(step => step.Order).Select(step => step.Kind)));
        Assert.Equal([12, 16, 20], published.Select(course => course.Exam!.Questions.Count));
        Assert.All(published, course => Assert.Equal(2,
            course.Exam!.Questions.Count(question => question.Kind == CourseTaskKind.SelfRecordedSpeech)));
        var listeningQuestions = published
            .SelectMany(course => course.Exam!.Questions)
            .Where(question => question.Skill == LanguageSkill.Listening)
            .ToArray();
        Assert.Equal(6, listeningQuestions.Length);
        Assert.All(listeningQuestions, question =>
        {
            Assert.False(string.IsNullOrWhiteSpace(question.AudioText));
            Assert.DoesNotContain(question.AudioText!, question.Prompt, StringComparison.Ordinal);
            Assert.NotEqual(question.Answer, question.AudioText);
        });
        Assert.Equal([GermanLevel.B1, GermanLevel.B2, GermanLevel.C1, GermanLevel.C2],
            catalog.Track.Courses.Where(course => course.Availability == CourseAvailability.Planned).Select(course => course.Level));
    }

    [Fact]
    public void Provenance_DeclaresOriginalContentAndNoCertificationClaim()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "WortBruecke.App", "Content", "course-provenance.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("original-lerntype-materials", root.GetProperty("contentPolicy").GetString());
        Assert.Equal("none", root.GetProperty("certificationClaim").GetString());
        Assert.True(root.GetProperty("sources").GetArrayLength() >= 6);
        Assert.Equal(3, root.GetProperty("localReferences").GetArrayLength());
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LernType.sln"))) return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("LernType.sln was not found.");
    }
}

using System.Text.Json;

namespace WortBruecke.Tests.Content;

public sealed class ExamBlueprintTests
{
    private static readonly HashSet<string> AllowedLevels =
        ["A0", "A1", "A2", "B1", "B2", "C1", "C2"];

    private static readonly HashSet<string> AllowedOfficialHosts =
        ["goethe.de", "www.goethe.de", "bfu.goethe.de", "telc.net", "www.telc.net", "shop.telc.net", "testdaf.de", "www.testdaf.de", "bamf.de", "www.bamf.de"];

    [Fact]
    public void ExamBlueprint_IsValidAndCoversA0ThroughC2()
    {
        using var document = LoadBlueprint();
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(DateOnly.TryParse(root.GetProperty("lastVerified").GetString(), out _));

        var stages = root.GetProperty("learningStages").EnumerateArray().ToArray();
        Assert.Equal(AllowedLevels.Order(), stages.Select(stage => stage.GetProperty("level").GetString()).Order());
        Assert.False(stages.Single(stage => stage.GetProperty("level").GetString() == "A0")
            .GetProperty("officialCertificate").GetBoolean());
        Assert.All(stages, stage => Assert.NotEmpty(stage.GetProperty("trainingTargets").EnumerateArray()));

        var providers = root.GetProperty("providers").EnumerateArray()
            .Select(provider => provider.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(["goethe", "telc", "gast", "bamf"]), providers);

        var exams = root.GetProperty("exams").EnumerateArray().ToArray();
        Assert.NotEmpty(exams);
        Assert.Equal(exams.Length, exams.Select(exam => exam.GetProperty("id").GetString()).Distinct().Count());

        foreach (var providerId in new[] { "goethe", "telc" })
        {
            var providerLevels = exams
                .Where(exam => exam.GetProperty("providerId").GetString() == providerId)
                .SelectMany(exam => exam.GetProperty("cefrLevels").EnumerateArray())
                .Select(level => level.GetString())
                .ToHashSet();
            Assert.Equal(new HashSet<string?>(["A1", "A2", "B1", "B2", "C1", "C2"]), providerLevels);
        }

        Assert.Contains(exams, exam => exam.GetProperty("id").GetString() == "testdaf-digital");
        Assert.Contains(exams, exam => exam.GetProperty("id").GetString() == "dtz-a2-b1");
        Assert.DoesNotContain(exams.SelectMany(exam => exam.GetProperty("cefrLevels").EnumerateArray()),
            level => level.GetString() == "A0");
    }

    [Fact]
    public void EveryExam_HasFourSkillsDurationsScoringTrainingAndOfficialSources()
    {
        using var document = LoadBlueprint();
        var root = document.RootElement;
        var sourceIds = root.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var exam in root.GetProperty("exams").EnumerateArray())
        {
            var examId = exam.GetProperty("id").GetString();
            Assert.Contains(exam.GetProperty("providerId").GetString(), new[] { "goethe", "telc", "gast", "bamf" });
            Assert.All(exam.GetProperty("cefrLevels").EnumerateArray(),
                level => Assert.Contains(level.GetString()!, AllowedLevels));

            var segments = exam.GetProperty("segments").EnumerateArray().ToArray();
            Assert.NotEmpty(segments);
            Assert.All(segments, segment =>
            {
                Assert.True(segment.GetProperty("durationMinutes").GetInt32() > 0, $"{examId} has a non-positive duration.");
                Assert.True(segment.GetProperty("parts").GetInt32() > 0, $"{examId} has a segment without parts.");
                Assert.NotEmpty(segment.GetProperty("taskFamilies").EnumerateArray());
            });

            var skills = segments.SelectMany(segment => segment.GetProperty("skills").EnumerateArray())
                .Select(skill => skill.GetString())
                .ToHashSet();
            foreach (var requiredSkill in new[] { "reading", "listening", "writing", "speaking" })
            {
                Assert.Contains(requiredSkill, skills);
            }

            Assert.False(string.IsNullOrWhiteSpace(exam.GetProperty("scoring").GetProperty("type").GetString()));
            Assert.NotEmpty(exam.GetProperty("appTrainingRequirements").EnumerateArray());
            var refs = exam.GetProperty("sourceRefs").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.NotEmpty(refs);
            Assert.All(refs, sourceRef => Assert.Contains(sourceRef!, sourceIds));
        }

        foreach (var source in root.GetProperty("sources").EnumerateArray())
        {
            Assert.True(Uri.TryCreate(source.GetProperty("url").GetString(), UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            Assert.Contains(uri.Host, AllowedOfficialHosts);
        }
    }

    [Fact]
    public void TestDaFAndDtz_KeepTheirNonBinaryOfficialScoringModels()
    {
        using var document = LoadBlueprint();
        var exams = document.RootElement.GetProperty("exams").EnumerateArray().ToArray();

        var testDaF = exams.Single(exam => exam.GetProperty("id").GetString() == "testdaf-digital");
        var testDaFScoring = testDaF.GetProperty("scoring");
        Assert.Equal("band-per-skill", testDaFScoring.GetProperty("type").GetString());
        Assert.False(testDaFScoring.GetProperty("universalPass").GetBoolean());
        Assert.Equal(4, testDaFScoring.GetProperty("bands").GetArrayLength());

        var dtz = exams.Single(exam => exam.GetProperty("id").GetString() == "dtz-a2-b1");
        var dtzScoring = dtz.GetProperty("scoring");
        Assert.Equal("dual-level-profile", dtzScoring.GetProperty("type").GetString());
        Assert.Equal(20, dtzScoring.GetProperty("receptiveCombined").GetProperty("a2MinCorrect").GetInt32());
        Assert.Equal(33, dtzScoring.GetProperty("receptiveCombined").GetProperty("b1MinCorrect").GetInt32());
    }

    private static JsonDocument LoadBlueprint()
    {
        var solutionRoot = FindSolutionRoot();
        var path = Path.Combine(solutionRoot, "src", "WortBruecke.App", "Content", "exams.json");
        Assert.True(File.Exists(path), $"Missing exam blueprint: {path}");
        return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the LernType solution root.");
    }
}

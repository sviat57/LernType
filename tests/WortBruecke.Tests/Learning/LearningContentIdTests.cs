using WortBruecke.Core.Learning;

namespace WortBruecke.Tests.Learning;

public sealed class LearningContentIdTests
{
    [Fact]
    public void ObjectiveId_IsStableAndCanonical()
    {
        var first = LearningContentId.FromObjective("A1.Reading.Notices");
        var second = LearningContentId.FromObjective("  a1.reading.notices  ");

        Assert.Equal(first, second);
        Assert.Equal(599651345753298062L, first);
        Assert.True(first > 0);
    }

    [Fact]
    public void Namespaces_KeepKindsAndSectionsDistinct()
    {
        var objective = LearningContentId.FromObjective("sample");
        var diagnostic = LearningContentId.FromDiagnostic("sample");
        var firstSection = LearningContentId.FromExamSection("exam", "reading");
        var secondSection = LearningContentId.FromExamSection("exam", "listening");

        Assert.Equal(4, new[] { objective, diagnostic, firstSection, secondSection }.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyStableKey_IsRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => LearningContentId.FromObjective(key));
    }
}

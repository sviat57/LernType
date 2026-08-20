using WortBruecke.Core.Training;

namespace WortBruecke.Tests.Training;

public sealed class GrammarHeuristicServiceTests
{
    private readonly GrammarHeuristicService _service = new();

    [Theory]
    [InlineData("perfekt", "Am Samstag habe ich meine Freundin besucht und wir haben zusammen gekocht.")]
    [InlineData("passiv", "Der Brief wird heute geschrieben.")]
    [InlineData("konjunktiv2", "Ich würde gern kommen, wenn ich mehr Zeit hätte.")]
    [InlineData("indirekte-rede", "Er sagte, er sei müde und habe keine Zeit.")]
    [InlineData("basic-sentence", "Ich heiße Lena.")]
    [InlineData("negation", "Ich habe keinen Hund. Er ist nicht groß.")]
    [InlineData("nominalisierung", "Durch die Erweiterung des Nahverkehrs wird eine Verringerung des Autoverkehrs erreicht.")]
    [InlineData("partizipialattribut", "Die sorgfältig geprüften Ergebnisse wurden in einem Bericht veröffentlicht.")]
    public void Analyze_FindsExpectedMarkers(string rule, string response)
    {
        var result = _service.Analyze(rule, response);

        Assert.True(result.HasExpectedMarkers);
        Assert.NotEmpty(result.FoundMarkers);
        Assert.Empty(result.MissingMarkers);
    }

    [Fact]
    public void Analyze_PerfektReportsWhichPartIsMissing()
    {
        var result = _service.Analyze("perfekt", "Am Samstag besuche ich meine Freundin.");

        Assert.False(result.HasExpectedMarkers);
        Assert.Contains("вспомогательный глагол haben/sein", result.MissingMarkers);
        Assert.Contains("Partizip II", result.MissingMarkers);
    }

    [Fact]
    public void Analyze_UnknownRuleReturnsActionableFallback()
    {
        var result = _service.Analyze("future-rule", "Ein Text");

        Assert.False(result.HasExpectedMarkers);
        Assert.Contains("LLM", result.Summary);
    }
}

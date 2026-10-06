using DeChimex.Shared.Constants;
using Xunit;

namespace DeChimex.UnitTests.Shared;

public class MarketingContentComposerTests
{
    [Fact]
    public void Build_RendersControlledBlocksAndPreservesOnlyFirstNameToken()
    {
        var result = MarketingContentComposer.Build("""
            {"blocks":[
              {"type":"heading","text":"Hello {{FirstName}}"},
              {"type":"paragraph","text":"A clear update for your print project."},
              {"type":"button","text":"View the shop","url":"https://dechimexprinthub.com/shop"},
              {"type":"divider"}
            ]}
            """);

        Assert.True(result.Success);
        Assert.Contains("<h2", result.Html);
        Assert.Contains("Hello {{FirstName}}", result.Html);
        Assert.Contains("href='https://dechimexprinthub.com/shop'", result.Html);
        Assert.Contains("View the shop", result.PlainText);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_EncodesMarkupAndAttributesInsteadOfInterpretingUserText()
    {
        var result = MarketingContentComposer.Build("""
            {"blocks":[{"type":"paragraph","text":"<img src=x onerror=alert(1)> & \"hello\""}]}
            """);

        Assert.True(result.Success);
        Assert.Contains("&lt;img", result.Html);
        Assert.DoesNotContain("<img src=x", result.Html);
        Assert.Contains("&amp;", result.Html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/image.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void Build_RejectsNonHttpsButtons(string url)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            blocks = new[] { new { type = "button", text = "Open", url } }
        });
        var result = MarketingContentComposer.Build(json);
        Assert.False(result.Success);
        Assert.Contains("HTTPS", result.Error);
    }

    [Fact]
    public void Build_RejectsUnsupportedPersonalizationAndMalformedDocuments()
    {
        var unknownToken = MarketingContentComposer.Build("""
            {"blocks":[{"type":"paragraph","text":"Hello {{Email}}"}]}
            """);
        Assert.False(unknownToken.Success);
        Assert.Contains("First name", unknownToken.Error);

        var malformed = MarketingContentComposer.Build("not json");
        Assert.False(malformed.Success);
        Assert.NotNull(malformed.Error);
    }

    [Fact]
    public void Build_RequiresReadableBlocksAndEnforcesBlockLimit()
    {
        Assert.False(MarketingContentComposer.Build("{\"blocks\":[]}").Success);
        var tooMany = System.Text.Json.JsonSerializer.Serialize(new
        {
            blocks = Enumerable.Range(0, 21).Select(_ => new { type = "divider" }).ToArray()
        });
        Assert.False(MarketingContentComposer.Build(tooMany).Success);
    }
}

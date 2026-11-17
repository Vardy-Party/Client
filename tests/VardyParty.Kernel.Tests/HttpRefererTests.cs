using Xunit;

namespace VardyParty.Kernel.Tests;

public class HttpRefererTests
{
    [Theory]
    [InlineData("https://player.example.test/player.html", null, "https://player.example.test/player.html")]
    [InlineData("http://page.example.test/match", null, "http://page.example.test/match")]
    [InlineData(" chrome-error://chromewebdata/ ", "https://live2.example.test/match/1", "https://live2.example.test/match/1")]
    [InlineData("about:blank", "https://page.example.test/", "https://page.example.test/")]
    [InlineData("data:text/html,hi", "https://page.example.test/", "https://page.example.test/")]
    [InlineData("blob:https://page.example.test/abc", "https://page.example.test/", "https://page.example.test/")]
    [InlineData("chrome://net-internals", "https://page.example.test/", "https://page.example.test/")]
    [InlineData("chrome-error://chromewebdata/", "about:blank", null)]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void Sanitize_DropsNonHttpAndFallsBack(string? value, string? fallback, string? expected)
    {
        // Arrange — value from InlineData

        // Act
        var sanitized = HttpReferer.Sanitize(value, fallback);

        // Assert
        Assert.Equal(expected, sanitized);
    }
}
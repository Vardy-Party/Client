using System;
using Xunit;

namespace VardyParty.SvgTextOutliner.Tests;

public class SvgTextOutlinerTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("middle")]
    [InlineData("end")]
    public void ReplaceTextElements_WhenAnchorIsStartMiddleOrEnd_WritesPathWithoutText(string anchor)
    {
        // Arrange
        var svg = SplashText($"text-anchor='{anchor}'");

        // Act
        var result = global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        AssertOutlined(result);
    }

    [Fact]
    public void ReplaceTextElements_WhenAnchorChanges_MovesTheOutline()
    {
        // Arrange
        var start = SplashText("text-anchor='start'");
        var middle = SplashText("text-anchor='middle'");
        var end = SplashText("text-anchor='end'");

        // Act
        var startResult = global::SvgTextOutliner.ReplaceTextElements(start);
        var middleResult = global::SvgTextOutliner.ReplaceTextElements(middle);
        var endResult = global::SvgTextOutliner.ReplaceTextElements(end);

        // Assert
        Assert.NotEqual(startResult, middleResult);
        Assert.NotEqual(middleResult, endResult);
        Assert.NotEqual(startResult, endResult);
    }

    [Theory]
    [InlineData("400")]
    [InlineData("600")]
    [InlineData("700")]
    public void ReplaceTextElements_WhenWeightIs400600Or700_WritesPathWithoutText(string weight)
    {
        // Arrange
        var svg = SplashText($"font-weight='{weight}'");

        // Act
        var result = global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        AssertOutlined(result);
    }

    [Fact]
    public void ReplaceTextElements_WhenWeightCrossesBoldThreshold_ChangesTheOutline()
    {
        // Arrange
        var regular = SplashText("font-weight='400'");
        var bold = SplashText("font-weight='700'");

        // Act
        var regularResult = global::SvgTextOutliner.ReplaceTextElements(regular);
        var boldResult = global::SvgTextOutliner.ReplaceTextElements(bold);

        // Assert
        Assert.NotEqual(regularResult, boldResult);
    }

    [Fact]
    public void ReplaceTextElements_WhenFamilyIsQuotedTwoWords_WritesPathWithoutText()
    {
        // Arrange
        var svg = SplashText("font-family=\"Liberation Sans\"");

        // Act
        var result = global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        AssertOutlined(result);
    }

    [Fact]
    public void ReplaceTextElements_WhenFamilyIsUnknown_OutlinesWithBundledFace()
    {
        // Arrange
        var svg = SplashText("font-family='No Such Face'");

        // Act
        var result = global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        AssertOutlined(result);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("font-size")]
    [InlineData("fill")]
    public void ReplaceTextElements_WhenRequiredAttributeIsMissing_Throws(string missing)
    {
        // Arrange
        var svg = SplashTextWithout(missing);

        // Act
        var act = () => global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<svg><text x='10' y='20' font-size='24' fill='#ffffff'><tspan>VardyParty</tspan></text></svg>")]
    [InlineData("<svg><text x='10' y='20' font-size='24' fill='#ffffff'/>")]
    [InlineData("<svg><text x='10' y='20' font-size='24' fill='#ffffff'>VardyParty</text><text x='10' y='40' font-size='18' fill='#ffffff'><tspan>More</tspan></text></svg>")]
    public void ReplaceTextElements_WhenTextElementIsNotSimple_Throws(string svg)
    {
        // Arrange
        // Act
        var act = () => global::SvgTextOutliner.ReplaceTextElements(svg);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    private static string SplashText(string extraAttributes) =>
        "<svg xmlns='http://www.w3.org/2000/svg'>"
        + $"<text x='256' y='330' font-size='44' fill='#ffffff' {extraAttributes}>VardyParty</text>"
        + "</svg>";

    private static string SplashTextWithout(string attribute)
    {
        var attributes = "x='256' y='330' font-size='44' fill='#ffffff'";
        var token = attribute switch
        {
            "x" => "x='256' ",
            "y" => "y='330' ",
            "font-size" => "font-size='44' ",
            "fill" => "fill='#ffffff'",
            _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Unexpected splash attribute."),
        };
        attributes = attributes.Replace(token, "", StringComparison.Ordinal);
        return $"<svg xmlns='http://www.w3.org/2000/svg'><text {attributes}>VardyParty</text></svg>";
    }

    private static void AssertOutlined(string result)
    {
        Assert.Contains("<path", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<text", result, StringComparison.OrdinalIgnoreCase);
    }
}

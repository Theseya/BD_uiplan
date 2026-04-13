using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class ParseHelpersTests
{
    [Theory]
    [InlineData("42", 42)]
    [InlineData("0", 0)]
    [InlineData("-1", -1)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData(" 12 ", 12)]
    public void IntOrNull_ParsesCorrectly(string? input, int? expected)
    {
        Assert.Equal(expected, ParseHelpers.IntOrNull(input));
    }

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("0", "0")]
    [InlineData("-2.5", "-2.5")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("abc", null)]
    public void DecimalOrNull_ParsesCorrectly(string? input, string? expectedStr)
    {
        var result = ParseHelpers.DecimalOrNull(input);
        if (expectedStr is null)
            Assert.Null(result);
        else
            Assert.Equal(decimal.Parse(expectedStr, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    [Fact]
    public void DecimalOrNull_WithComma_EventuallyParsesViaNormalization()
    {
        // Implementation tries InvariantCulture, then CurrentCulture, then replaces comma with dot
        var result = ParseHelpers.DecimalOrNull("1,5");
        Assert.NotNull(result);
        Assert.True(result == 1.5m || result == 15m);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("да", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("other", false)]
    public void BoolOrNull_ParsesCorrectly(string? input, bool? expected)
    {
        Assert.Equal(expected, ParseHelpers.BoolOrNull(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("Бизнес-информатика", "бизнес информатика")]
    [InlineData("OP Name 123", "op name 123")]
    [InlineData("  multiple   spaces  ", "multiple spaces")]
    public void NormalizeOpName_NormalizesCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, ParseHelpers.NormalizeOpName(input));
    }

    [Fact]
    public void H_EscapesHtml()
    {
        Assert.Equal("&lt;script&gt;", ParseHelpers.H("<script>"));
        Assert.Equal("&amp;", ParseHelpers.H("&"));
        Assert.Equal("&quot;", ParseHelpers.H("\""));
        Assert.Equal("", ParseHelpers.H(null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("123", true)]
    [InlineData("0", true)]
    [InlineData("999999", true)]
    [InlineData("12a", false)]
    [InlineData("abc", false)]
    [InlineData("1.5", false)]
    [InlineData("12 34", false)]
    [InlineData("-1", false)]
    public void IsValidDisciplineNo_ValidatesCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, ParseHelpers.IsValidDisciplineNo(input));
    }
}

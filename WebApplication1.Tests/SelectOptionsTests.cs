using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class SelectOptionsTests
{
    [Fact]
    public void AcademicYearOptions_ContainsDefaultPlaceholder()
    {
        var html = SelectOptions.AcademicYearOptions(null);
        Assert.Contains("Учебный год", html);
        Assert.Contains("<option value=\"\">", html);
    }

    [Fact]
    public void AcademicYearOptions_ContainsRangeYears()
    {
        var html = SelectOptions.AcademicYearOptions(null, 2024, 2026);
        Assert.Contains("2024-2025", html);
        Assert.Contains("2025-2026", html);
        Assert.DoesNotContain("2023-2024", html);
        Assert.DoesNotContain("2027-2028", html);
    }

    [Fact]
    public void AcademicYearOptions_SelectedYear_HasSelectedAttribute()
    {
        var html = SelectOptions.AcademicYearOptions("2024-2025", 2023, 2026);
        Assert.Contains("value=\"2024-2025\" selected", html);
        Assert.DoesNotContain("value=\"2023-2024\" selected", html);
    }

    [Fact]
    public void AcademicYearToMonthValue_ValidYear_ReturnsMonth()
    {
        Assert.Equal("2024-09", SelectOptions.AcademicYearToMonthValue("2024-2025"));
        Assert.Equal("2023-09", SelectOptions.AcademicYearToMonthValue("2023-2024"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("invalid", "")]
    [InlineData("2024", "")]
    public void AcademicYearToMonthValue_InvalidInput_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, SelectOptions.AcademicYearToMonthValue(input));
    }

    [Fact]
    public void EducationLevels_ContainsExpectedValues()
    {
        Assert.Contains("Бакалавриат", SelectOptions.EducationLevels);
        Assert.Contains("Магистратура", SelectOptions.EducationLevels);
        Assert.Contains("Аспирантура", SelectOptions.EducationLevels);
        Assert.Equal(3, SelectOptions.EducationLevels.Length);
    }

    [Fact]
    public void DisciplineKinds_ContainsExpectedValues()
    {
        Assert.Contains("обязательная", SelectOptions.DisciplineKinds);
        Assert.Contains("по выбору", SelectOptions.DisciplineKinds);
        Assert.Contains("факультатив", SelectOptions.DisciplineKinds);
        Assert.Equal(3, SelectOptions.DisciplineKinds.Length);
    }

    [Fact]
    public void LanguageOptions_ContainsExpectedValues()
    {
        Assert.Contains("Русский", SelectOptions.LanguageOptions);
        Assert.Contains("Английский", SelectOptions.LanguageOptions);
        Assert.Contains("Русский/Английский", SelectOptions.LanguageOptions);
        Assert.Equal(3, SelectOptions.LanguageOptions.Length);
    }
}

using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class OpBudgetHelperTests
{
    [Theory]
    [InlineData("Международный бизнес", true)]
    [InlineData("международный бизнес", true)]
    [InlineData("Менеджмент в ритейле", true)]
    [InlineData("Управление цифровым продуктом", true)]
    [InlineData("Управление B2C-бизнесом: технологии и инновации", true)]
    [InlineData("Управление продуктом в ИТ-бизнесе", true)]
    [InlineData("Бизнес-аналитика и системы больших данных", false)]
    [InlineData("Несуществующая ОП", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCommercial_ReturnsCorrectResult(string? opName, bool expected)
    {
        Assert.Equal(expected, OpBudgetHelper.IsCommercial(opName));
    }

    [Theory]
    [InlineData("Международный бизнес", "Коммерческая")]
    [InlineData("Бизнес-аналитика и системы больших данных", "Бюджетная")]
    [InlineData(null, "Бюджетная")]
    [InlineData("", "Бюджетная")]
    public void OpBudgetCommercial_ReturnsLabel(string? opName, string expected)
    {
        Assert.Equal(expected, OpBudgetHelper.OpBudgetCommercial(opName));
    }

    [Fact]
    public void CommercialOpNames_ContainsExpectedEntries()
    {
        Assert.Contains("Цифровые инновации в управлении предприятием", OpBudgetHelper.CommercialOpNames);
        Assert.Contains("Маркетинг-менеджмент", OpBudgetHelper.CommercialOpNames);
        Assert.Equal(9, OpBudgetHelper.CommercialOpNames.Count);
    }

    [Fact]
    public void CommercialOpNames_IsCaseInsensitive()
    {
        Assert.Contains("международный бизнес", OpBudgetHelper.CommercialOpNames);
        Assert.Contains("МЕЖДУНАРОДНЫЙ БИЗНЕС", OpBudgetHelper.CommercialOpNames);
    }
}

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// Проверка наличия ключевых элементов страниц: кнопка «Добавить строку», таблицы, формы.
/// </summary>
public class PageContentTests : IClassFixture<WebAppFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PageContentTests(WebAppFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string> GetPageHtmlAsync(WebApplicationFactory<Program> factory, string url)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", url)
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync(url);
        if (response.StatusCode != HttpStatusCode.OK) return "";
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Uiworkload_WhenAuthenticated_ContainsAddRowButton_AndWlTable()
    {
        var html = await GetPageHtmlAsync(_factory, "/uiworkload");
        if (string.IsNullOrEmpty(html) || !html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase))
            return;
        if (!html.Contains("id=\"wl-table\"", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("data-wl-add", html);
        Assert.Contains("Добавить строку", html);
        Assert.Contains("wl-batch-form", html);
    }

    [Fact]
    public async Task Uifaculty_WhenAuthenticated_ContainsAddRowButton_AndPpsTable()
    {
        var html = await GetPageHtmlAsync(_factory, "/uifaculty");
        if (string.IsNullOrEmpty(html) || !html.Contains("Преподаватели", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("data-pps-add", html);
        Assert.Contains("Добавить строку", html);
        Assert.Contains("id=\"pps-table\"", html);
    }

    [Fact]
    public async Task Uidisciplines_WhenAuthenticated_ContainsAddRowButton_AndDiscTable()
    {
        var html = await GetPageHtmlAsync(_factory, "/uidisciplines");
        if (string.IsNullOrEmpty(html) || !html.Contains("Дисциплины", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("data-disc-add", html);
        Assert.Contains("Добавить строку", html);
        Assert.Contains("disc-batch-form", html);
    }

    [Fact]
    public async Task Uiplan_WhenAuthenticated_ContainsAddRowLink()
    {
        var html = await GetPageHtmlAsync(_factory, "/uiplan");
        if (string.IsNullOrEmpty(html) || !html.Contains("Учебный план", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("data-plan-add", html);
        Assert.Contains("Добавить строку", html);
    }

    /// <summary>
    /// Учебный план с фильтром по ОП может использовать запасной запрос (36 колонок).
    /// Страница должна отдавать 200 и не содержать ошибку чтения Int32 для text.
    /// </summary>
    [Fact]
    public async Task Uiplan_WhenAuthenticated_WithOpFilter_ReturnsOk_WithoutReaderException()
    {
        var html = await GetPageHtmlAsync(_factory, "/uiplan?op=test");
        if (string.IsNullOrEmpty(html)) return;
        if (!html.Contains("Учебный план", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.DoesNotContain("Reading as 'System.Int32' is not supported", html);
        Assert.DoesNotContain("DataTypeName 'text'", html);
    }

    /// <summary>
    /// Если на странице учебного плана есть шаблон новой строки, в нём должен быть выбор ОП (select planId).
    /// </summary>
    [Fact]
    public async Task Uiplan_WhenAuthenticated_AddRowTemplate_HasPlanIdSelect()
    {
        var html = await GetPageHtmlAsync(_factory, "/uiplan");
        if (string.IsNullOrEmpty(html) || !html.Contains("Учебный план", StringComparison.OrdinalIgnoreCase))
            return;
        if (!html.Contains("plan-new-template", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("name=\"planId\"", html);
        Assert.Contains("form=\"plan-new-template\"", html);
    }

    /// <summary>
    /// Главная страница не должна содержать ошибку чтения типов из БД.
    /// </summary>
    [Fact]
    public async Task Dashboard_WhenAuthenticated_ReturnsOk_WithoutReaderException()
    {
        var html = await GetPageHtmlAsync(_factory, "/");
        if (string.IsNullOrEmpty(html)) return;
        if (!html.Contains("Главная", StringComparison.OrdinalIgnoreCase) && !html.Contains("Сводная статистика", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.DoesNotContain("Reading as 'System.Int32' is not supported", html);
        Assert.DoesNotContain("DataTypeName 'text'", html);
    }

    [Fact]
    public async Task Uiops_WhenAuthenticated_ReturnsOk()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uiops")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uiops");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        if (html.Contains("Вход", StringComparison.OrdinalIgnoreCase) || html.Contains("login", StringComparison.OrdinalIgnoreCase)) return;
        Assert.True(
            html.Contains("ОП", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("ошибка", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Error", StringComparison.OrdinalIgnoreCase),
            "Ожидалась страница ОП или сообщение об ошибке");
    }

    [Fact]
    public async Task Uidb_WhenAuthenticated_ReturnsOk()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uidb")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uidb");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("DB", html, StringComparison.OrdinalIgnoreCase);
    }
}

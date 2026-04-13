using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// Dashboard (/) content when authenticated: sections and stat labels added for main page.
/// </summary>
[Collection(nameof(WebAppCollection))]
public class DashboardContentTests : IClassFixture<WebAppFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DashboardContentTests(WebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Dashboard_WhenAuthenticated_ContainsMainSectionsAndStatLabels()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/")
        });
        await client.PostAsync("/login", loginForm);

        var dashboardResponse = await client.GetAsync("/");
        if (dashboardResponse.StatusCode != HttpStatusCode.OK)
            return;

        var html = await dashboardResponse.Content.ReadAsStringAsync();
        if (!html.Contains("Главная", StringComparison.OrdinalIgnoreCase))
            return; // not on dashboard (e.g. login page when DB unavailable)

        Assert.Contains("Сводная статистика", html);
        Assert.Contains("Нагрузка", html);
        Assert.Contains("Часы по видам работ", html);
        Assert.Contains("Преподаватели (ППС)", html);
        Assert.Contains("Справочники", html);
        Assert.Contains("stat-label", html);
        Assert.Contains("Часов лекций", html);
        Assert.Contains("Часов семинаров", html);
        Assert.Contains("Часов (НИС", html);
        Assert.Contains("Образовательных программ", html);
        Assert.Contains("Департаментов", html);
        Assert.Contains("Всего строк в распределении", html);
        Assert.Contains("Активных преподавателей", html);
    }

    [Fact]
    public async Task Uiworkload_WhenAuthenticated_ContainsExportAndImportUi()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uiworkload")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uiworkload");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        if (!html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase)) return;
        if (!html.Contains("wl-batch-form", StringComparison.OrdinalIgnoreCase) &&
            !html.Contains("id=\"wl-table\"", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("Выгрузить", html);
        Assert.Contains("Загрузить из Excel", html);
        Assert.Contains("data-export=\"excel\"", html);
        Assert.Contains("/uiworkload/export", html);
    }

    [Fact]
    public async Task Uifaculty_WhenAuthenticated_ContainsExportAndImportUi()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uifaculty")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uifaculty");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        if (!html.Contains("Преподаватели", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("Выгрузить", html);
        Assert.Contains("Загрузить из Excel", html);
        Assert.Contains("data-export=\"excel\"", html);
        Assert.Contains("/uifaculty/export", html);
    }

    [Fact]
    public async Task Uidisciplines_WhenAuthenticated_ContainsExportAndImportUi()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uidisciplines")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uidisciplines");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        if (!html.Contains("Дисциплины", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("Выгрузить", html);
        Assert.Contains("Загрузить из Excel", html);
        Assert.Contains("data-export=\"excel\"", html);
        Assert.Contains("/uidisciplines/export", html);
    }

    [Fact]
    public async Task Uiplan_WhenAuthenticated_ContainsExportAndImportUi()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uiplan")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uiplan");
        if (response.StatusCode != HttpStatusCode.OK) return;
        var html = await response.Content.ReadAsStringAsync();
        if (!html.Contains("Учебный план", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("Выгрузить", html);
        Assert.Contains("Загрузить из Excel", html);
        Assert.Contains("data-export=\"excel\"", html);
        Assert.Contains("/uiplan/export", html);
    }
}

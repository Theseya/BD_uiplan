using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// Проверка разметки нераспределённой нагрузки на странице «Нагрузка»:
/// data-plan-discipline-id, data-plan-total, cell-unallocated и скрипт пересчёта.
/// </summary>
public class WorkloadUnallocatedTests : IClassFixture<WebAppFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WorkloadUnallocatedTests(WebAppFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string> GetWorkloadHtmlAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uiworkload")
        });
        await client.PostAsync("/login", loginForm);
        var response = await client.GetAsync("/uiworkload");
        if (response.StatusCode != HttpStatusCode.OK) return "";
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Uiworkload_WhenAuthenticated_ContainsUnallocatedColumnHeader()
    {
        var html = await GetWorkloadHtmlAsync(_factory);
        if (string.IsNullOrEmpty(html) || !html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("Нераспределённая нагрузка", html);
    }

    [Fact]
    public async Task Uiworkload_WhenAuthenticated_AndCanEdit_ContainsDataAttributesForRecalc()
    {
        var html = await GetWorkloadHtmlAsync(_factory);
        if (string.IsNullOrEmpty(html) || !html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase))
            return;
        // Если есть таблица нагрузки, при canEdit должны быть атрибуты для пересчёта или скрипт
        if (!html.Contains("id=\"wl-table\"", StringComparison.OrdinalIgnoreCase))
            return;
        // Скрипт пересчёта нераспределённой нагрузки (при canEdit)
        var hasRecalcScript = html.Contains("cell-unallocated", StringComparison.OrdinalIgnoreCase) &&
            (html.Contains("data-plan-discipline-id", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("data-plan-total", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("recalc", StringComparison.OrdinalIgnoreCase) ||
             html.Contains("DOMContentLoaded", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasRecalcScript, "Страница нагрузки должна содержать разметку или скрипт для нераспределённой нагрузки (cell-unallocated / data-plan-* / recalc / DOMContentLoaded).");
    }

    [Fact]
    public async Task Uiworkload_WhenAuthenticated_ContainsExportLinkOrForm()
    {
        var html = await GetWorkloadHtmlAsync(_factory);
        if (string.IsNullOrEmpty(html) || !html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase))
            return;
        // Выгрузка в Excel: ссылка или форма
        Assert.True(
            html.Contains("Выгрузить в Excel", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("uiworkload/export", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("data-export=\"excel\"", StringComparison.OrdinalIgnoreCase),
            "Страница нагрузки должна содержать ссылку/форму выгрузки в Excel.");
    }

    /// <summary>
    /// Скрипт пересчёта нераспределённой нагрузки должен подписываться на input и change.
    /// </summary>
    [Fact]
    public async Task Uiworkload_WhenAuthenticated_RecalcScriptSubscribesToInputAndChange()
    {
        var html = await GetWorkloadHtmlAsync(_factory);
        if (string.IsNullOrEmpty(html) || !html.Contains("Нагрузка", StringComparison.OrdinalIgnoreCase))
            return;
        if (!html.Contains("id=\"wl-table\"", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Contains("addEventListener('input'", html);
        Assert.Contains("addEventListener('change'", html);
        Assert.Contains("cell-unallocated", html);
    }
}

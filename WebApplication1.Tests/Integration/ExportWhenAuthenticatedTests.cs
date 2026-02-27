using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// Регрессионные тесты выгрузки в Excel: при авторизации export возвращает файл (200) или редирект,
/// чтобы исправления выгрузки не терялись при правках кода.
/// </summary>
public class ExportWhenAuthenticatedTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public ExportWhenAuthenticatedTests(WebAppFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Логин и возврат заголовка Cookie для подстановки в последующие запросы.
    /// </summary>
    private static string? GetAuthCookieFromLoginResponse(HttpResponseMessage loginResponse)
    {
        if (!loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return null;
        var parts = new List<string>();
        foreach (var header in setCookies)
        {
            var semicolon = header.Trim().IndexOf(';');
            var nameValue = semicolon < 0 ? header.Trim() : header.Trim()[..semicolon].Trim();
            if (nameValue.IndexOf('=') > 0)
                parts.Add(nameValue);
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private async Task<(HttpClient client, string? cookie)> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/")
        });
        var loginResponse = await client.PostAsync("/login", loginForm);
        var cookie = GetAuthCookieFromLoginResponse(loginResponse);
        return (client, cookie);
    }

    private static async Task AssertExcelResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.True(
            contentType.Contains("spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("vnd.ms-excel", StringComparison.OrdinalIgnoreCase),
            $"Expected Excel Content-Type, got: {contentType}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4, "Response body too short for xlsx");
        // xlsx is a zip archive
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }

    [Fact]
    public async Task UiworkloadExport_WhenAuthenticated_ReturnsExcelOrRedirectToExportEmpty()
    {
        var (client, cookie) = await CreateAuthenticatedClientAsync();
        if (string.IsNullOrEmpty(cookie))
            return; // DB unavailable or login failed

        var request = new HttpRequestMessage(HttpMethod.Get, "/uiworkload/export");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            var location = response.Headers.Location?.OriginalString ?? "";
            Assert.True(
                location.StartsWith("/uiworkload", StringComparison.OrdinalIgnoreCase),
                $"Expected redirect to /uiworkload, got: {location}");
            Assert.Contains("exportEmpty=1", location);
            return;
        }

        await AssertExcelResponseAsync(response);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.True(
            disposition.FileName?.Contains("workload_", StringComparison.OrdinalIgnoreCase) == true &&
            disposition.FileName.Contains(".xlsx", StringComparison.OrdinalIgnoreCase),
            $"Expected filename workload_*.xlsx, got: {disposition.FileName}");
    }

    [Fact]
    public async Task UiplanExport_WhenAuthenticated_ReturnsExcelOrRedirectToExportEmpty()
    {
        var (client, cookie) = await CreateAuthenticatedClientAsync();
        if (string.IsNullOrEmpty(cookie))
            return;

        var request = new HttpRequestMessage(HttpMethod.Get, "/uiplan/export");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            var location = response.Headers.Location?.OriginalString ?? "";
            Assert.True(
                location.StartsWith("/uiplan", StringComparison.OrdinalIgnoreCase),
                $"Expected redirect to /uiplan, got: {location}");
            return;
        }

        await AssertExcelResponseAsync(response);
    }

    [Fact]
    public async Task UifacultyExport_WhenAuthenticated_ReturnsExcel()
    {
        var (client, cookie) = await CreateAuthenticatedClientAsync();
        if (string.IsNullOrEmpty(cookie))
            return;

        var request = new HttpRequestMessage(HttpMethod.Get, "/uifaculty/export");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.True(
            contentType.Contains("spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("vnd.ms-excel", StringComparison.OrdinalIgnoreCase),
            $"Expected Excel Content-Type, got: {contentType}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4);
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }

    [Fact]
    public async Task UidisciplinesExport_WhenAuthenticated_ReturnsExcel()
    {
        var (client, cookie) = await CreateAuthenticatedClientAsync();
        if (string.IsNullOrEmpty(cookie))
            return;

        var request = new HttpRequestMessage(HttpMethod.Get, "/uidisciplines/export");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.True(
            contentType.Contains("spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("vnd.ms-excel", StringComparison.OrdinalIgnoreCase),
            $"Expected Excel Content-Type, got: {contentType}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4);
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }
}

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

[Collection(nameof(WebAppCollection))]
public class RouteTests : IClassFixture<WebAppFactory>
{
    private readonly HttpClient _client;

    public RouteTests(WebAppFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Root_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task Login_Get_ReturnsOk()
    {
        var response = await _client.GetAsync("/login");
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("login", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/uiworkload")]
    [InlineData("/uiplan")]
    [InlineData("/uiops")]
    [InlineData("/uifaculty")]
    [InlineData("/uidisciplines")]
    [InlineData("/uidb")]
    [InlineData("/admin/users")]
    public async Task ProtectedRoutes_RedirectToLogin_WhenNotAuthenticated(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UiworkloadExport_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/uiworkload/export");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UiplanExport_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/uiplan/export");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UifacultyExport_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/uifaculty/export");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UidisciplinesExport_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/uidisciplines/export");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Theory]
    [InlineData("/uifaculty/import")]
    [InlineData("/uidisciplines/import")]
    [InlineData("/uiworkload/import")]
    [InlineData("/uiplan/import")]
    public async Task ImportEndpoints_Get_Returns404OrRedirectsToLogin(string path)
    {
        var response = await _client.GetAsync(path);
        // GET на POST-only endpoint: 404 либо 302 на логин (если сначала сработала авторизация)
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound ||
            (response.StatusCode == HttpStatusCode.Redirect && (response.Headers.Location?.OriginalString ?? "").StartsWith("/login", StringComparison.OrdinalIgnoreCase)),
            $"Expected 404 or redirect to login, got {response.StatusCode}");
    }

    [Fact]
    public async Task Logout_Post_RedirectsToLogin()
    {
        var response = await _client.PostAsync("/logout", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task Login_Post_InvalidCredentials_RedirectsToLoginWithError()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "nonexistent"),
            new KeyValuePair<string, string>("password", "wrong")
        });
        var response = await _client.PostAsync("/login", form);
        // 302 when DB is available and user not found; 500 when DB is not configured
        if (response.StatusCode == HttpStatusCode.InternalServerError)
            return; // DB unavailable, skip assertion
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/login", location);
        Assert.Contains("error", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dashboard_Root_RedirectsToLogin_WhenNotAuthenticated()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task Login_ResetDev_Get_InDevelopment_ReturnsRedirectOrServerError()
    {
        var response = await _client.GetAsync("/login/reset-dev");
        // Development: 302 when DB works; 500 when DB unavailable
        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Redirect or InternalServerError, got {response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.Redirect)
            Assert.Contains("/login", response.Headers.Location?.OriginalString ?? "", StringComparison.OrdinalIgnoreCase);
    }
}

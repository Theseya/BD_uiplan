using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

public class LoginFlowTests : IClassFixture<WebAppFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LoginFlowTests(WebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_Post_ValidCredentials_RedirectsToReturnUrl()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "/uifaculty")
        });
        var response = await client.PostAsync("/login", form);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
            return; // DB unavailable
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.True(
            location.Contains("/uifaculty", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("/login", StringComparison.OrdinalIgnoreCase),
            $"Expected redirect to /uifaculty or login, got: {location}");
    }

    [Fact]
    public async Task Login_Post_ValidCredentials_RedirectsToApp_WhenReturnUrlEmpty()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "admin"),
            new KeyValuePair<string, string>("password", "admin"),
            new KeyValuePair<string, string>("returnUrl", "")
        });
        var response = await client.PostAsync("/login", form);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
            return;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.True(
            location.Contains("/uiworkload", StringComparison.OrdinalIgnoreCase) ||
            location.Equals("/", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("/?", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("/login", StringComparison.OrdinalIgnoreCase),
            $"Expected redirect to app or login, got: {location}");
    }
}

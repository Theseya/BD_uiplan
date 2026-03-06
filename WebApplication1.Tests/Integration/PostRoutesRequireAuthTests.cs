using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// POST endpoints that require auth must redirect to login when not authenticated.
/// </summary>
[Collection(nameof(WebAppCollection))]
public class PostRoutesRequireAuthTests : IClassFixture<WebAppFactory>
{
    private readonly HttpClient _client;

    public PostRoutesRequireAuthTests(WebAppFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task UiworkloadDelete_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("assignmentId", "1") });
        var response = await _client.PostAsync("/uiworkload/delete", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UiworkloadSaveBatch_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("rowKey", "1") });
        var response = await _client.PostAsync("/uiworkload/save-batch-form", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UifacultySave_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("ppsName", "Test"),
            new KeyValuePair<string, string>("departmentId", "1")
        });
        var response = await _client.PostAsync("/uifaculty/save", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UifacultyDelete_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("facultyId", "1") });
        var response = await _client.PostAsync("/uifaculty/delete", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UidisciplinesDelete_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("planDisciplineId", "1") });
        var response = await _client.PostAsync("/uidisciplines/delete", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UidisciplinesSaveBatch_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("rowKey", "1") });
        var response = await _client.PostAsync("/uidisciplines/save-batch-form", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task AdminUsersCreate_WithoutAuth_RedirectsToLogin()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", "u"),
            new KeyValuePair<string, string>("password", "p")
        });
        var response = await _client.PostAsync("/admin/users/create", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UifacultyImport_WithoutAuth_RedirectsToLogin()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(""), "redirectQuery");
        var response = await _client.PostAsync("/uifaculty/import", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UidisciplinesImport_WithoutAuth_RedirectsToLogin()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(""), "redirectQuery");
        form.Add(new StringContent("1"), "planId");
        var response = await _client.PostAsync("/uidisciplines/import", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UiworkloadImport_WithoutAuth_RedirectsToLogin()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(""), "redirectQuery");
        var response = await _client.PostAsync("/uiworkload/import", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task UiplanImport_WithoutAuth_RedirectsToLogin()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(""), "redirectQuery");
        var response = await _client.PostAsync("/uiplan/import", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.OriginalString ?? "");
    }
}

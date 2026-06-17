using System.Net;
using Xunit;

namespace WebApplication1.Tests.Integration;

[Collection(nameof(LiveWebAppCollection))]
public class LiveRoleTests : IClassFixture<LiveWebAppFactory>
{
    private readonly LiveWebAppFactory _factory;

    public LiveRoleTests(LiveWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task DepartmentManager_CanAccessWorkloadAndFaculty()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, "/uiworkload");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/uiworkload")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/uifaculty")).StatusCode);
    }

    [Fact]
    public async Task DepartmentManager_CannotAccessAdminUsers()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, "/uiworkload", allowAutoRedirect: false);
        var response = await client.GetAsync("/admin/users");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Добавить пользователя", html);
        }
    }

    [Fact]
    public async Task DepartmentManager_WorkloadPage_HasImportControls()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, "/uiworkload");
        var response = await client.GetAsync("/uiworkload");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Загрузить из Excel", html);
        Assert.Contains("data-wl-add", html);
    }

    [Fact]
    public async Task OpManager_CanAccessPlanAndDisciplines()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.OpManagerLogin, "/uiplan");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/uiplan")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/uidisciplines")).StatusCode);
    }

    [Fact]
    public async Task OpManager_WorkloadPage_LacksEditImportControls()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.OpManagerLogin, "/uiworkload");
        var response = await client.GetAsync("/uiworkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Нагрузка", html);
        Assert.DoesNotContain("toolbar-import", html);
        Assert.DoesNotContain("data-wl-add", html);
    }

    [Fact]
    public async Task OpManager_CannotAccessDeptDisciplineRequests()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.OpManagerLogin, "/uiplan", allowAutoRedirect: false);
        var response = await client.GetAsync("/uidept-discipline-requests");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DepartmentManager_CanAccessDeptDisciplineRequests()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, "/uidept-discipline-requests");
        var response = await client.GetAsync("/uidept-discipline-requests");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Согласование дисциплин", html);
    }

    [Fact]
    public async Task DepartmentManager_SaveFaculty_IsAllowed()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, "/uifaculty", allowAutoRedirect: false);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("ppsName", "TEST_ROLE_" + Guid.NewGuid().ToString("N")[..8]),
            new KeyValuePair<string, string>("departmentId", "1")
        });
        var response = await client.PostAsync("/uifaculty/save", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/uifaculty", response.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task OpManager_SaveFaculty_IsForbidden()
    {
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.OpManagerLogin, "/uifaculty", allowAutoRedirect: false);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("ppsName", "TEST_FORBIDDEN"),
            new KeyValuePair<string, string>("departmentId", "1")
        });
        var response = await client.PostAsync("/uifaculty/save", form);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            var location = response.Headers.Location?.OriginalString ?? "";
            Assert.DoesNotContain("saved", location, StringComparison.OrdinalIgnoreCase);
        }
    }
}

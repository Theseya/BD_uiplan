using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace WebApplication1.Tests.Integration;

/// <summary>
/// End-to-end checks against the test PostgreSQL database (appsettings.Test.json).
/// </summary>
[Collection(nameof(LiveWebAppCollection))]
public class LiveSystemTests : IClassFixture<LiveWebAppFactory>
{
    private readonly LiveWebAppFactory _factory;
    private readonly string _connectionString;

    public LiveSystemTests(LiveWebAppFactory factory)
    {
        _factory = factory;
        _connectionString = LiveTestConfig.ConnectionString;
    }

    private async Task AssertDatabaseReachableAsync()
    {
        await using var conn = await LiveTestAuthHelper.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task LiveDb_IsReachable() => await AssertDatabaseReachableAsync();

    [Fact]
    public async Task LiveDb_HasCoreTablesAndView()
    {
        await AssertDatabaseReachableAsync();
        await using var conn = await LiveTestAuthHelper.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN (
              'departments','educational_programs','faculty_members','plan_disciplines',
              'teaching_assignments','assignment_hours','app_users','work_types'
            );
            """, conn);
        Assert.Equal(8, Convert.ToInt32(await cmd.ExecuteScalarAsync()));

        await using var viewCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.views WHERE table_schema='public' AND table_name='v_workload_by_worktype'", conn);
        Assert.Equal(1, Convert.ToInt32(await viewCmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task LiveDb_HasNoOrphanRecords()
    {
        await AssertDatabaseReachableAsync();
        await using var conn = await LiveTestAuthHelper.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT
              (SELECT COUNT(*) FROM teaching_assignments ta LEFT JOIN plan_disciplines pd ON pd.plan_discipline_id = ta.plan_discipline_id WHERE pd.plan_discipline_id IS NULL) +
              (SELECT COUNT(*) FROM assignment_hours ah LEFT JOIN teaching_assignments ta ON ta.assignment_id = ah.assignment_id WHERE ta.assignment_id IS NULL) +
              (SELECT COUNT(*) FROM user_allowed_ops uao LEFT JOIN educational_programs ep ON ep.name = uao.op_name WHERE ep.op_id IS NULL)
            """, conn);
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Login_Admin_Succeeds()
    {
        await AssertDatabaseReachableAsync();
        await LiveTestAuthHelper.EnsureUserPasswordAsync(LiveTestConfig.DefaultAdminLogin);
        var (login, password) = LiveTestAuthHelper.GetCredentials();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", login),
            new KeyValuePair<string, string>("password", password)
        });
        var response = await client.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Theory]
    [InlineData("/", "Главная")]
    [InlineData("/uiworkload", "Нагрузка")]
    [InlineData("/uiplan", "Учебный план")]
    [InlineData("/uifaculty", "Преподаватели")]
    [InlineData("/uidisciplines", "Дисциплины")]
    [InlineData("/uiops", "ОП")]
    [InlineData("/admin/users", "Пользователи")]
    [InlineData("/uidb", "DB")]
    public async Task ProtectedPages_ReturnOk_WhenAuthenticated(string path, string expectedFragment)
    {
        await AssertDatabaseReachableAsync();
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(_factory, returnUrl: path);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedFragment, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reading as 'System.Int32' is not supported", html);
        Assert.DoesNotContain("DataTypeName 'text'", html);
    }

    [Fact]
    public async Task Dashboard_ContainsStatistics()
    {
        await AssertDatabaseReachableAsync();
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(_factory, returnUrl: "/");
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Сводная статистика", html);
        Assert.Contains("stat-label", html);
    }

    [Fact]
    public async Task Uiworkload_ContainsTableAndUnallocatedColumn()
    {
        await AssertDatabaseReachableAsync();
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(_factory, returnUrl: "/uiworkload");
        var response = await client.GetAsync("/uiworkload");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"wl-table\"", html);
        Assert.Contains("Нераспределённая нагрузка", html);
        Assert.Contains("Выгрузить", html);
        Assert.Contains("Загрузить из Excel", html);
    }

    [Theory]
    [InlineData("/uiworkload")]
    [InlineData("/uiplan")]
    [InlineData("/uifaculty")]
    [InlineData("/uidisciplines")]
    public async Task ImportLabels_RenderSvgIcon_NotLiteralPlaceholder(string path)
    {
        await AssertDatabaseReachableAsync();
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(_factory, returnUrl: path);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Выберите файл", html);
        Assert.DoesNotContain("${IconImport}", html);
        Assert.Contains("<svg", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/uifaculty/export")]
    [InlineData("/uidisciplines/export")]
    public async Task ExportEndpoints_ReturnValidXlsx(string path)
    {
        await AssertDatabaseReachableAsync();
        var cookie = await LiveTestAuthHelper.GetAuthCookieAsync(_factory);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4);
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
    }
}

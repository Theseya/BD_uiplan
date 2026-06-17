using System.Net;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace WebApplication1.Tests.Integration;

[Collection(nameof(LiveWebAppCollection))]
public class LiveMutationTests : IClassFixture<LiveWebAppFactory>
{
    private readonly LiveWebAppFactory _factory;

    public LiveMutationTests(LiveWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FacultySave_InsertsRowIntoDatabase()
    {
        var uniqueName = "TEST_SAVE_" + Guid.NewGuid().ToString("N")[..10];
        await using var conn = await LiveTestAuthHelper.OpenConnectionAsync();
        await using var deptCmd = new NpgsqlCommand(
            "SELECT department_id FROM departments WHERE name = 'Департамент маркетинга' LIMIT 1", conn);
        var deptId = Convert.ToInt32(await deptCmd.ExecuteScalarAsync());

        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(
            _factory, LiveTestConfig.DepartmentManagerLogin, returnUrl: "/uifaculty", allowAutoRedirect: false);
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("ppsName", uniqueName),
            new KeyValuePair<string, string>("departmentId", deptId.ToString()),
            new KeyValuePair<string, string>("position", "Преподаватель")
        });
        var response = await client.PostAsync("/uifaculty/save", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/uifaculty", location);
        Assert.DoesNotContain("err=", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FacultyImport_FromXlsx_InsertsRowIntoDatabase()
    {
        var uniqueName = "TEST_IMPORT_" + Guid.NewGuid().ToString("N")[..10];
        var xlsx = LiveTestExcelHelper.CreateFacultyImportWorkbook(uniqueName, "Департамент маркетинга");
        var client = await LiveTestAuthHelper.CreateAuthenticatedClientAsync(_factory, returnUrl: "/uifaculty", allowAutoRedirect: false);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(""), "redirectQuery");
        var fileContent = new ByteArrayContent(xlsx);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(fileContent, "file", "test-faculty.xlsx");

        var response = await client.PostAsync("/uifaculty/import", content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/uifaculty", response.Headers.Location?.OriginalString ?? "");

        await using var conn = await LiveTestAuthHelper.OpenConnectionAsync();
        await using var read = new NpgsqlCommand(
            "SELECT faculty_id FROM faculty_members WHERE full_name = @name", conn);
        read.Parameters.AddWithValue("name", NpgsqlDbType.Text, uniqueName);
        var id = await read.ExecuteScalarAsync();
        Assert.NotNull(id);

        await using var del = new NpgsqlCommand("DELETE FROM faculty_members WHERE faculty_id = @id", conn);
        del.Parameters.AddWithValue("id", NpgsqlDbType.Integer, Convert.ToInt32(id));
        await del.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Notifications_Count_ReturnsJsonForAuthenticatedUser()
    {
        var cookie = await LiveTestAuthHelper.GetAuthCookieAsync(_factory);
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/count");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("count", body, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace WebApplication1.Tests;

public static class LiveTestAuthHelper
{
    public static (string login, string password) GetCredentials(string? login = null)
    {
        login ??= Environment.GetEnvironmentVariable("WEBAPP_TEST_LOGIN") ?? LiveTestConfig.DefaultAdminLogin;
        var password = Environment.GetEnvironmentVariable("WEBAPP_TEST_PASSWORD") ?? LiveTestConfig.DefaultTestPassword;
        return (login, password);
    }

    public static async Task EnsureUserPasswordAsync(string login, string? password = null)
    {
        password ??= LiveTestConfig.DefaultTestPassword;
        await using var conn = await OpenConnectionAsync();
        await using var read = new NpgsqlCommand("SELECT password_hash FROM app_users WHERE login = @login", conn);
        read.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
        var existing = await read.ExecuteScalarAsync() as string;
        if (existing is not null && AuthHelpers.VerifyPassword(password, existing))
            return;

        var hash = AuthHelpers.HashPassword(password);
        await using var cmd = new NpgsqlCommand(
            "UPDATE app_users SET password_hash = @hash WHERE login = @login", conn);
        cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
        cmd.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
        var updated = await cmd.ExecuteNonQueryAsync();
        Assert.True(updated > 0, $"User '{login}' not found in app_users.");
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> factory,
        string? login = null,
        string returnUrl = "/",
        bool allowAutoRedirect = true)
    {
        var (userLogin, password) = GetCredentials(login);
        await EnsureUserPasswordAsync(userLogin, password);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", userLogin),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("returnUrl", returnUrl)
        });
        var loginResponse = await client.PostAsync("/login", loginForm);
        Assert.NotEqual(HttpStatusCode.InternalServerError, loginResponse.StatusCode);
        return client;
    }

    public static async Task<string> GetAuthCookieAsync(WebApplicationFactory<Program> factory, string? login = null)
    {
        var (userLogin, password) = GetCredentials(login);
        await EnsureUserPasswordAsync(userLogin, password);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginForm = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("login", userLogin),
            new KeyValuePair<string, string>("password", password)
        });
        var loginResponse = await client.PostAsync("/login", loginForm);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.True(loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies));
        return string.Join("; ", cookies!.Select(c => c.Split(';')[0]));
    }

    public static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = await new NpgsqlDataSourceBuilder(LiveTestConfig.ConnectionString).Build().OpenConnectionAsync();
        return conn;
    }
}

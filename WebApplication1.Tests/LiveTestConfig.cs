using Microsoft.Extensions.Configuration;

namespace WebApplication1.Tests;

public static class LiveTestConfig
{
    public const string DefaultAdminLogin = "admin";
    public const string DefaultTestPassword = "TestPass1!";
    public const string DepartmentManagerLogin = "dep_dm";
    public const string OpManagerLogin = "op_bi";

    public static string ConnectionString => ResolveConnectionString()
        ?? throw new InvalidOperationException(
            "Live test connection string not found. Set ConnectionStrings__Default or create appsettings.Test.json.");

    public static string ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? Environment.GetEnvironmentVariable("WEBAPP_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        foreach (var file in new[] { "appsettings.Test.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path))
                path = Path.Combine(Directory.GetCurrentDirectory(), file);
            if (!File.Exists(path))
                continue;

            var cs = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build()
                .GetConnectionString("Default");
            if (!string.IsNullOrWhiteSpace(cs))
                return cs;
        }

        return null!;
    }
}

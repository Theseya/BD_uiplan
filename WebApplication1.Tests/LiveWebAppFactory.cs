using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace WebApplication1.Tests;

/// <summary>
/// Test host wired to appsettings.Test.json (isolated test database).
/// </summary>
public class LiveWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile("appsettings.Test.json", optional: true, reloadOnChange: false);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = LiveTestConfig.ResolveConnectionString()
            });
        });
    }
}

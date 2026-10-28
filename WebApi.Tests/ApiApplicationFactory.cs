using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace WebApi.Tests;

public class ApiApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly string TestJwtKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = TestDatabase.PostgresConnectionString,
                ["ConnectionStrings:Redis"] = TestDatabase.RedisConnectionString,
                ["Jwt:Key"] = TestJwtKey
            });
        });
    }
}

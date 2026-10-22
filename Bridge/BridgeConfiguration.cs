using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Bridge;

internal static class BridgeConfiguration
{
    public static HostApplicationBuilder CreateApplicationBuilder(string[] args, string contentRootPath)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = contentRootPath });
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        var environmentToken = Environment.GetEnvironmentVariable("LICHESS_TOKEN");

        if (string.IsNullOrWhiteSpace(environmentToken) is false)
        {
            builder.Configuration["Lichess:Token"] = environmentToken;
        }

        builder.Configuration.AddCommandLine(args);

        return builder;
    }
}

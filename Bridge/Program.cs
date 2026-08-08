using LichessBridge;
using LichessBridge.Bridge;
using LichessBridge.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var environmentToken = Environment.GetEnvironmentVariable("LICHESS_TOKEN");
if (string.IsNullOrWhiteSpace(environmentToken) is false)
{
    builder.Configuration["Lichess:Token"] = environmentToken;
}

builder.Logging.ClearProviders();
var logFilePath = builder.Configuration["Logging:FilePath"];

if (string.IsNullOrWhiteSpace(logFilePath) is false)
{
    builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));
}

builder.Services.AddLichessBridge(builder.Configuration);

using var host = builder.Build();
var statusDisplay = host.Services.GetRequiredService<IStatusDisplay>();
statusDisplay.Show("Connecting to Lichess...");

await host.RunAsync();

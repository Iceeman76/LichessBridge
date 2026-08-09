using Bridge;
using LichessBridge;
using LichessBridge.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

if (Console.IsOutputRedirected is false)
{
    AnsiConsole.Clear();
}

var builder = Host.CreateApplicationBuilder(args);
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

builder.Services.AddSingleton<ILoggerProvider, DashboardLoggerProvider>();
builder.Services.AddLichessBridge(builder.Configuration);
builder.Services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
builder.Services.AddHostedService<ConsoleInputService>();

using var host = builder.Build();
var statusDisplay = host.Services.GetRequiredService<IStatusDisplay>();
statusDisplay.Show("Connecting to Lichess...");

await host.RunAsync();

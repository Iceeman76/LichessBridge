using LichessBridge;
using LichessBridge.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var environmentToken = Environment.GetEnvironmentVariable("LICHESS_TOKEN");
if (!string.IsNullOrWhiteSpace(environmentToken))
    builder.Configuration["Lichess:Token"] = environmentToken;

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
builder.Services.AddLichessBridge(builder.Configuration);

using var host = builder.Build();
var engine = host.Services.GetRequiredService<IOptions<EngineOptions>>().Value;

Console.WriteLine("LichessBridge");
Console.WriteLine(new string('─', 28));
Console.WriteLine();
Console.WriteLine($"Engine : {engine.Name}");
Console.WriteLine("Lichess: connecting...");
Console.WriteLine();

await host.RunAsync();

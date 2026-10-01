using System.Net;
using FaNotify.Configuration;
using FaNotify.FurAffinity;
using FaNotify.Hosting;
using FaNotify.Http;
using FaNotify.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var logPath = Path.Combine("/logs", $"fa-notify-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
using var loggerProvider = new AppLoggerProvider(logPath);
var startupLogger = loggerProvider.CreateLogger("Startup");
startupLogger.WritingLogFile(logPath);

AppConfig config;
try
{
    config = AppConfig.Load();
}
catch (Exception exception)
{
    startupLogger.ConfigurationFailed(exception.Message);
    throw;
}

var cookieContainer = CookieFile.Load(config.CookieFile);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(loggerProvider);
builder.Logging.SetMinimumLevel(config.LogLevel);
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(cookieContainer);

// The default HTTP logging handlers would log full request URLs, including the Discord webhook token.
builder.Services.AddHttpClient(HttpClientNames.Fa, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        CookieContainer = cookieContainer,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .RemoveAllLoggers();

builder.Services.AddHttpClient(HttpClientNames.Discord, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
    .RemoveAllLoggers();

builder.Services.AddHttpClient(HttpClientNames.Solver, client => client.Timeout = TimeSpan.FromSeconds(90))
    .RemoveAllLoggers();

builder.Services.AddHostedService<NotifierWorker>();

await builder.Build().RunAsync();

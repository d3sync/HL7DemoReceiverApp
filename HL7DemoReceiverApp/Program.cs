using System;
using System.IO;
using System.Threading;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System.Drawing;
using Serilog.Sinks.SystemConsole.Themes;

namespace HL7ProxyBridge;

internal class Program
{
    static void Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true)
                      .AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<Hl7Settings>(context.Configuration.GetSection("Hl7"));
                services.AddSingleton<Hl7ListenerService>();
                services.AddSingleton<Hl7ClientService>();
                services.AddSingleton<Hl7ProxyService>();
                services.AddSingleton<IHl7ListenerService>(sp =>
                {
                    var settings = sp.GetRequiredService<IOptions<Hl7Settings>>().Value;
                    var mode = context.Configuration.GetValue<string>("Hl7:Mode")?.ToLowerInvariant();
                    if (mode == "proxy")
                        return sp.GetRequiredService<Hl7ProxyService>();
                    return settings.IsServer ? sp.GetRequiredService<Hl7ListenerService>() : sp.GetRequiredService<Hl7ClientService>();
                });
            })
            .UseSerilog((context, services, configuration) =>
            {
                var settings = context.Configuration.GetSection("Hl7").Get<Hl7Settings>();
                configuration
                .WriteTo.Console(theme: AnsiConsoleTheme.Literate)
                    .WriteTo.File(settings.LogFilePath.Replace("{Date}", DateTime.Now.ToString("yyyyMMdd")))
                    .Enrich.FromLogContext();
            })
            .Build();

        var listener = host.Services.GetRequiredService<IHl7ListenerService>();
        listener.Run();
    }
}

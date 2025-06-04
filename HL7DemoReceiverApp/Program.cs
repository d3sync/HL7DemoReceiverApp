using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System.Drawing;
using Serilog.Sinks.SystemConsole.Themes;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Threading.Tasks;

namespace HL7ProxyBridge;

internal class Program
{
    static void Main(string[] args)
    {
        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception occurred");
        };
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Fatal(e.Exception, "Unobserved task exception occurred");
            e.SetObserved();
        };

        try
        {
            Log.Information("Application starting");
            var exePath = AppContext.BaseDirectory;
            var configFile = Path.Combine(exePath, "appsettings.json");

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile(configFile, optional: false, reloadOnChange: true)
                          //.AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true)
                          .AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    services.Configure<Hl7Settings>(context.Configuration.GetSection("Hl7"));
                    var mode = context.Configuration.GetValue<string>("Hl7:Mode")?.ToLowerInvariant();
                    if (mode == "proxy")
                        services.AddHostedService<Hl7ProxyService>();
                    else if (mode == "client")
                        services.AddHostedService<Hl7ClientService>();
                    else
                        services.AddHostedService<Hl7ListenerService>();
                })
                .UseSerilog((context, services, configuration) =>
                {
                    var settings = context.Configuration.GetSection("Hl7").Get<Hl7Settings>();
                    configuration
                    .WriteTo.Console(theme: AnsiConsoleTheme.Literate)
                        .WriteTo.File(settings.LogFilePath.Replace("{Date}", DateTime.Now.ToString("yyyyMMdd")))
                        .Enrich.FromLogContext();
                })
                .UseWindowsService() // Enable running as a Windows Service
                .Build();

            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
        }
        finally
        {
            Log.Information("Application shutting down");
            Log.CloseAndFlush();
        }
    }
}

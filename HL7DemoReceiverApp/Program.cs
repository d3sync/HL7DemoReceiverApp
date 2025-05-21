using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using System.Drawing;
using Serilog.Sinks.SystemConsole.Themes;

namespace HL7DemoReceiverApp
{
    public class Hl7Settings
    {
        public int Port { get; set; }
        public string SendingApplication { get; set; } = string.Empty;
        public string SendingFacility { get; set; } = string.Empty;
        public string ReceivingApplication { get; set; } = string.Empty;
        public string ReceivingFacility { get; set; } = string.Empty;
        public string LogFilePath { get; set; } = string.Empty;
        public string[] AllowedEvents { get; set; } = Array.Empty<string>();
        public string AckMode { get; set; } = "AA";
        public string MessageDateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
        public bool DisconnectAfterAck { get; set; } = false;
    }

    public interface IHl7ListenerService
    {
        void Run();
    }

    public class Hl7ListenerService : IHl7ListenerService
    {
        private readonly Hl7Settings _settings;
        private readonly Serilog.ILogger _logger;
        private const byte VT = 0x0B;
        private const byte FS = 0x1C;
        private const byte CR = 0x0D;
        private TcpListener? _listener;
        private bool _running = true;

        public Hl7ListenerService(IOptions<Hl7Settings> options, Serilog.ILogger logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public void Run()
        {
            Console.CancelKeyPress += (s, e) => { _running = false; _listener?.Stop(); };
            Directory.CreateDirectory(Path.GetDirectoryName(_settings.LogFilePath) ?? "logs");
            _listener = new TcpListener(IPAddress.Any, _settings.Port);
            _listener.Start();
            Console.WriteLine($"HL7 MLLP Listener started on port {_settings.Port}.");
            try
            {
                while (_running)
                {
                    if (!_listener.Pending()) { Thread.Sleep(100); continue; }
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Listener error");
            }
            finally
            {
                _listener.Stop();
            }
        }

        private void HandleClient(object? obj)
        {
            using var client = obj as TcpClient;
            if (client == null) return;
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Console.WriteLine($"Client connected: {endpoint}");
            _logger.Information("Client connected: {Endpoint}", endpoint);
            try
            {
                using var stream = client.GetStream();
                var buffer = new List<byte>();
                while (_running && client.Connected)
                {
                    int b = stream.ReadByte();
                    if (b == -1) break;
                    if (b == VT)
                    {
                        buffer.Clear();
                        // Read until FS+CR
                        while (true)
                        {
                            int data = stream.ReadByte();
                            if (data == -1) break;
                            if (data == FS)
                            {
                                int next = stream.ReadByte();
                                if (next == CR)
                                    break;
                                if (next != -1) buffer.Add((byte)data);
                                if (next != -1) buffer.Add((byte)next);
                                continue;
                            }
                            buffer.Add((byte)data);
                        }
                        string hl7 = Encoding.ASCII.GetString(buffer.ToArray());
                        string nowFmt = DateTime.Now.ToString(_settings.MessageDateTimeFormat);
                        Console.WriteLine($"Received HL7 message from {endpoint} at {nowFmt}");
                        _logger.Information("Received HL7 message from {Endpoint}: {Message}", endpoint, hl7);
                        _logger.Debug("HL7 message content: {Message}", hl7);
                        string controlId = ExtractMSH10(hl7);
                        string ack = BuildAck(hl7, controlId);
                        Console.WriteLine($"Sending ACK to {endpoint} at {nowFmt}");
                        _logger.Information("Sending ACK to {Endpoint}: {Ack}", endpoint, ack);
                        byte[] ackBytes = FrameMLLP(ack);
                        stream.Write(ackBytes, 0, ackBytes.Length);
                        if (_settings.DisconnectAfterAck)
                        {
                            Console.WriteLine($"Disconnecting client {endpoint} after ACK as per configuration.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Client error");
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"Client disconnected: {endpoint}");
                _logger.Information("Client disconnected: {Endpoint}", endpoint);
            }
        }

        private string ExtractMSH10(string hl7)
        {
            var lines = hl7.Split('\r');
            var msh = lines.FirstOrDefault(l => l.StartsWith("MSH"));
            if (msh == null) return string.Empty;
            var sep = msh[3];
            var fields = msh.Split(sep);
            return fields.Length > 9 ? fields[9] : string.Empty;
        }

        private string BuildAck(string incoming, string controlId)
        {
            var msh = incoming.Split('\r').FirstOrDefault(l => l.StartsWith("MSH"));
            char sep = msh != null && msh.Length > 3 ? msh[3] : '|';
            string encodingChars = msh != null && msh.Length > 7 ? msh.Substring(4, 4) : "^~\\&";
            string sendingApp = msh?.Split(sep).ElementAtOrDefault(5) ?? _settings.SendingApplication;
            string sendingFac = msh?.Split(sep).ElementAtOrDefault(6) ?? _settings.SendingFacility;
            string receivingApp = msh?.Split(sep).ElementAtOrDefault(3) ?? _settings.ReceivingApplication;
            string receivingFac = msh?.Split(sep).ElementAtOrDefault(4) ?? _settings.ReceivingFacility;
            string timestamp = DateTime.Now.ToString(_settings.MessageDateTimeFormat, CultureInfo.InvariantCulture);
            string ackMsg =
                $"MSH{sep}{encodingChars}{sep}{receivingApp}{sep}{receivingFac}{sep}{sendingApp}{sep}{sendingFac}{sep}{timestamp}{sep}{sep}ACK^R01{sep}{controlId}{sep}P{sep}2.3.1\r" +
                $"MSA{sep}{_settings.AckMode}{sep}{controlId}\r";
            return ackMsg;
        }

        private byte[] FrameMLLP(string message)
        {
            var msgBytes = Encoding.ASCII.GetBytes(message);
            var framed = new byte[msgBytes.Length + 3];
            framed[0] = VT;
            Buffer.BlockCopy(msgBytes, 0, framed, 1, msgBytes.Length);
            framed[framed.Length - 2] = FS;
            framed[framed.Length - 1] = CR;
            return framed;
        }

    }

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
                    services.AddSingleton<IHl7ListenerService, Hl7ListenerService>();
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
}

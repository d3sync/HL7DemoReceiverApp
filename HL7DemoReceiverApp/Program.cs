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
        public bool IsServer { get; set; } = true;
        public string ClientHost { get; set; } = string.Empty;
        public int ClientPort { get; set; } = 0;
    }

    public interface IHl7ListenerService
    {
        void Run();
    }

    public class Hl7ClientService : IHl7ListenerService
    {
        private readonly Hl7Settings _settings;
        private readonly Serilog.ILogger _logger;
        public Hl7ClientService(IOptions<Hl7Settings> options, Serilog.ILogger logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public void Run()
        {
            while (true)
            {
                try
                {
                    Console.WriteLine($"Connecting to HL7 MLLP server at {_settings.ClientHost}:{_settings.ClientPort}...");
                    using var client = new TcpClient();
                    client.Connect(_settings.ClientHost, _settings.ClientPort);
                    using var stream = client.GetStream();
                    Console.WriteLine("Connected to server. Type 'send' to send a message, or wait to receive HL7 messages. Type 'exit' to quit.");
                    var receiveThread = new Thread(() => ReceiveLoop(stream));
                    receiveThread.Start();
                    bool shouldExit = false;
                    while (true)
                    {
                        string? input = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(input)) continue;
                        if (input.Trim().ToLower() == "exit") { shouldExit = true; break; }
                        if (input.Trim().ToLower() == "send")
                        {
                            Console.WriteLine("Paste HL7 message (no MLLP framing). End with a blank line:");
                            var sb = new StringBuilder();
                            string? line;
                            while (!string.IsNullOrEmpty(line = Console.ReadLine()))
                            {
                                sb.AppendLine(line);
                            }
                            string message = sb.ToString().Replace("\n", "").Replace("\r\n", "\r").TrimEnd('\r');
                            byte[] framed = FrameMLLP(message);
                            stream.Write(framed, 0, framed.Length);
                            _logger.Information("Sent HL7 message: {Message}", message);
                            Console.WriteLine($"Sent HL7 message at {DateTime.Now.ToString(_settings.MessageDateTimeFormat)}");
                        }
                    }
                    // Wait for receive thread to finish
                    receiveThread.Join();
                    if (shouldExit) break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Client mode error");
                    Console.WriteLine($"Client mode error: {ex.Message}");
                }
                Console.WriteLine("Reconnecting in 1 second...");
                Thread.Sleep(1000);
            }
        }

        private void ReceiveLoop(NetworkStream stream)
        {
            var buffer = new List<byte>();
            try
            {
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b == -1) break;
                    if (b == 0x0B) // VT
                    {
                        buffer.Clear();
                        while (true)
                        {
                            int data = stream.ReadByte();
                            if (data == -1) break;
                            if (data == 0x1C) // FS
                            {
                                int next = stream.ReadByte();
                                if (next == 0x0D) // CR
                                    break;
                                if (next != -1) buffer.Add((byte)data);
                                if (next != -1) buffer.Add((byte)next);
                                continue;
                            }
                            buffer.Add((byte)data);
                        }
                        string hl7 = Encoding.ASCII.GetString(buffer.ToArray());
                        Console.WriteLine($"Received HL7 message at {DateTime.Now.ToString(_settings.MessageDateTimeFormat)}:");
                        Console.WriteLine(hl7);
                        _logger.Information("Received HL7 message: {Message}", hl7);
                        string controlId = ExtractMSH10(hl7);
                        string ack = BuildAck(hl7, controlId);
                        byte[] ackBytes = FrameMLLP(ack);
                        stream.Write(ackBytes, 0, ackBytes.Length);
                        Console.WriteLine($"Sent ACK at {DateTime.Now.ToString(_settings.MessageDateTimeFormat)}:");
                        Console.WriteLine(ack);
                        _logger.Information("Sent ACK: {Ack}", ack);
                        if (_settings.DisconnectAfterAck)
                        {
                            Console.WriteLine("DisconnectAfterAck is true. Closing client connection.");
                            stream.Close();
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Receive loop error");
                Console.WriteLine($"Receive loop error: {ex.Message}");
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
            framed[0] = 0x0B;
            Buffer.BlockCopy(msgBytes, 0, framed, 1, msgBytes.Length);
            framed[framed.Length - 2] = 0x1C;
            framed[framed.Length - 1] = 0x0D;
            return framed;
        }
    }

    public class Hl7ListenerService : IHl7ListenerService
    {
        private readonly Hl7Settings _settings;
        private readonly Serilog.ILogger _logger;

        public Hl7ListenerService(IOptions<Hl7Settings> options, Serilog.ILogger logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public void Run()
        {
            try
            {
                Console.WriteLine($"Starting HL7 MLLP listener on port {_settings.Port}...");
                var listener = new TcpListener(IPAddress.Any, _settings.Port);
                listener.Start();
                Console.WriteLine("Listener started. Waiting for connections...");
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    Console.WriteLine("Client connected.");
                    var thread = new Thread(() => HandleClient(client));
                    thread.Start();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Listener mode error");
                Console.WriteLine($"Listener mode error: {ex.Message}");
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new List<byte>();
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b == -1) break;
                    if (b == 0x0B) // VT
                    {
                        buffer.Clear();
                        while (true)
                        {
                            int data = stream.ReadByte();
                            if (data == -1) break;
                            if (data == 0x1C) // FS
                            {
                                int next = stream.ReadByte();
                                if (next == 0x0D) // CR
                                    break;
                                if (next != -1) buffer.Add((byte)data);
                                if (next != -1) buffer.Add((byte)next);
                                continue;
                            }
                            buffer.Add((byte)data);
                        }
                        string message = Encoding.ASCII.GetString(buffer.ToArray());
                        Console.WriteLine($"Received HL7 message at {DateTime.Now.ToString(_settings.MessageDateTimeFormat)}:");
                        Console.WriteLine(message);
                        _logger.Information("Received HL7 message: {Message}", message);
                        if (_settings.AllowedEvents.Contains(GetEventType(message)))
                        {
                            string ack = GenerateAck(message);
                            byte[] framedAck = FrameMLLP(ack);
                            stream.Write(framedAck, 0, framedAck.Length);
                            Console.WriteLine($"Sent ACK at {DateTime.Now.ToString(_settings.MessageDateTimeFormat)}:");
                            Console.WriteLine(ack);
                            _logger.Information("Sent ACK: {Ack}", ack);
                            if (_settings.DisconnectAfterAck)
                            {
                                Console.WriteLine("DisconnectAfterAck is true. Closing client connection.");
                                break;
                            }
                        }
                        else if (_settings.DisconnectAfterAck)
                        {
                            // If not allowed event but still want to disconnect after receiving
                            Console.WriteLine("DisconnectAfterAck is true. Closing client connection.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error handling client");
                Console.WriteLine($"Error handling client: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        private string GetEventType(string message)
        {
            var segments = message.Split('\r');
            var msh = segments.FirstOrDefault(s => s.StartsWith("MSH"));
            if (msh != null)
            {
                var fields = msh.Split('|');
                if (fields.Length > 8)
                    return fields[8];
            }
            return string.Empty;
        }

        private string GenerateAck(string message)
        {
            var segments = message.Split('\r');
            var msh = segments.FirstOrDefault(s => s.StartsWith("MSH"));
            if (msh != null)
            {
                var fields = msh.Split('|');
                fields[9] = _settings.AckMode;
                return string.Join("|", fields);
            }
            return string.Empty;
        }

        private byte[] FrameMLLP(string message)
        {
            var msgBytes = Encoding.ASCII.GetBytes(message);
            var framed = new byte[msgBytes.Length + 3];
            framed[0] = 0x0B;
            Buffer.BlockCopy(msgBytes, 0, framed, 1, msgBytes.Length);
            framed[framed.Length - 2] = 0x1C;
            framed[framed.Length - 1] = 0x0D;
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
                    services.AddSingleton<Hl7ListenerService>();
                    services.AddSingleton<Hl7ClientService>();
                    services.AddSingleton<IHl7ListenerService>(sp =>
                    {
                        var settings = sp.GetRequiredService<IOptions<Hl7Settings>>().Value;
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
}

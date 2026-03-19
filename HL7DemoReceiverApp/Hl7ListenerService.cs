using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace HL7ProxyBridge
{
    public class Hl7ListenerService : BackgroundService
    {
        private readonly Hl7Settings _settings;
        private readonly Serilog.ILogger _logger;

        public Hl7ListenerService(IOptions<Hl7Settings> options, Serilog.ILogger logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.Information("Starting HL7 MLLP listener on port {Port}...", _settings.Port);
                var listener = new TcpListener(IPAddress.Any, _settings.Port);
                listener.Start();
                _logger.Information("Listener started. Waiting for connections...");
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (listener.Pending())
                    {
                        var client = await listener.AcceptTcpClientAsync(stoppingToken);
                        _logger.Information("Client connected <<<< {EndPoint}", client.Client.LocalEndPoint?.ToString());
                        _ = Task.Run(() => HandleClient(client, stoppingToken), stoppingToken);
                    }
                    else
                    {
                        await Task.Delay(100, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Error(ex, "Listener mode error");
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken stoppingToken)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new List<byte>();
                while (!stoppingToken.IsCancellationRequested)
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
                        _logger.Information("Received HL7 message at {Time}:", DateTime.Now.ToString(_settings.MessageDateTimeFormat));
                        _logger.Information("{Message}", message);
                        if (_settings.AllowedEvents.Contains(GetEventType(message)))
                        {
                            string ack = GenerateAck(message);
                            byte[] framedAck = FrameMLLP(ack);
                            await stream.WriteAsync(framedAck, 0, framedAck.Length, stoppingToken);
                            _logger.Information("Sent ACK at {Time}:", DateTime.Now.ToString(_settings.MessageDateTimeFormat));
                            _logger.Information("{Ack}", ack);
                            if (_settings.DisconnectAfterAck)
                            {
                                _logger.Information("DisconnectAfterAck is true. Closing client connection.");
                                break;
                            }
                        }
                        else if (_settings.DisconnectAfterAck)
                        {
                            _logger.Information("DisconnectAfterAck is true. Closing client connection.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error handling client");
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
}

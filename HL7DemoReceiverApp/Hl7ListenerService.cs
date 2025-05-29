using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace HL7ProxyBridge
{
    public interface IHl7ListenerService
    {
        void Run();
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
                    Console.WriteLine($"Client connected <<<< {client.Client.LocalEndPoint?.ToString()}");
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
}

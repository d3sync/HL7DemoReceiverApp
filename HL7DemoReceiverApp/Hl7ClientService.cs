using System.Net.Sockets;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace HL7ProxyBridge;

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
                _logger.Information("Connecting to HL7 MLLP server at {Host}:{Port}...", _settings.ClientHost, _settings.ClientPort);
                using var client = new TcpClient();
                client.Connect(_settings.ClientHost, _settings.ClientPort);
                using var stream = client.GetStream();
                _logger.Information("Connected to server. Type 'send' to send a message, or wait to receive HL7 messages. Type 'exit' to quit.");
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
                        _logger.Information("Paste HL7 message (no MLLP framing). End with a blank line:");
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
                        _logger.Information("Sent HL7 message at {Time}", DateTime.Now.ToString(_settings.MessageDateTimeFormat));
                    }
                }
                // Wait for receive thread to finish
                receiveThread.Join();
                if (shouldExit) break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Client mode error");
            }
            _logger.Information("Reconnecting in 1 second...");
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
                    _logger.Information("Received HL7 message at {Time}:", DateTime.Now.ToString(_settings.MessageDateTimeFormat));
                    _logger.Information("{Message}", hl7);
                    string controlId = ExtractMSH10(hl7);
                    string ack = BuildAck(hl7, controlId);
                    byte[] ackBytes = FrameMLLP(ack);
                    stream.Write(ackBytes, 0, ackBytes.Length);
                    _logger.Information("Sent ACK at {Time}:", DateTime.Now.ToString(_settings.MessageDateTimeFormat));
                    _logger.Information("{Ack}", ack);
                    if (_settings.DisconnectAfterAck)
                    {
                        _logger.Information("DisconnectAfterAck is true. Closing client connection.");
                        stream.Close();
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Receive loop error");
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

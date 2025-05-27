using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace HL7ProxyBridge;

public class Hl7ProxyService : IHl7ListenerService
{
    private readonly Hl7Settings _settings;
    private readonly Serilog.ILogger _logger;
    private TcpClient? _clientSide;
    private NetworkStream? _clientStream;
    private TcpListener? _listenerSide;
    private readonly ConcurrentQueue<string> _outboundBuffer = new();
    private volatile bool _clientConnected = false;
    private volatile bool _running = true;
    private NetworkStream? _lastListenerStream; // For forwarding from client to listener

    public Hl7ProxyService(IOptions<Hl7Settings> options, Serilog.ILogger logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public void Run()
    {
        if (_settings.ProxyDirection.ToLowerInvariant() == "clienttolistener")
            ClientToListenerProxy();
        else
            ListenerToClientProxy();
    }

    // Analyzer (listener) -> Proxy -> LIS (client)
    private void ListenerToClientProxy()
    {
        var listener = new TcpListener(IPAddress.Any, _settings.Port);
        listener.Start();
        Console.WriteLine($"Proxy listening for analyzer on port {_settings.Port}...");
        var lisThread = new Thread(ConnectToClientSide);
        lisThread.Start();
        while (_running)
        {
            var analyzerClient = listener.AcceptTcpClient();
            Console.WriteLine("Analyzer connected.");
            var thread = new Thread(() => HandleListenerSide(analyzerClient));
            thread.Start();
        }
    }

    // Proxy connects as client to analyzer, listens for LIS
    private void ClientToListenerProxy()
    {
        var listenerThread = new Thread(ListenForListenerSide);
        listenerThread.Start();
        while (_running)
        {
            try
            {
                _clientSide = new TcpClient();
                _clientSide.Connect(_settings.ClientHost, _settings.ClientPort);
                _clientStream = _clientSide.GetStream();
                _clientConnected = true;
                Console.WriteLine($"[Proxy] Connected to analyzer at {_settings.ClientHost}:{_settings.ClientPort}");
                // Receive from analyzer and forward to LIS
                while (_clientConnected && _clientSide.Connected)
                {
                    var buffer = new List<byte>();
                    int b = _clientStream.ReadByte();
                    if (b == -1) { _clientConnected = false; break; }
                    if (b == 0x0B)
                    {
                        buffer.Clear();
                        while (true)
                        {
                            int data = _clientStream.ReadByte();
                            if (data == -1) { _clientConnected = false; break; }
                            if (data == 0x1C)
                            {
                                int next = _clientStream.ReadByte();
                                if (next == 0x0D) break;
                                if (next != -1) buffer.Add((byte)data);
                                if (next != -1) buffer.Add((byte)next);
                                continue;
                            }
                            buffer.Add((byte)data);
                        }
                        string hl7 = Encoding.ASCII.GetString(buffer.ToArray());
                        if (Hl7Utils.IsAckMessage(hl7))
                        {
                            Console.WriteLine($"[Proxy] Intercepted ACK from analyzer, not forwarding.");
                            _logger.Information("[Proxy] Intercepted ACK from analyzer, not forwarding.");
                        }
                        else
                        {
                            Console.WriteLine($"[Proxy] Received from analyzer, forwarding to LIS: {hl7}");
                            _logger.Information($"[Proxy] Received from analyzer, forwarding to LIS: {hl7}");
                            _outboundBuffer.Enqueue(hl7);
                            TrySendToListenerSide();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Proxy] Error: {ex.Message}");
                _logger.Error(ex, "[Proxy] Error occurred while connecting to analyzer.");
            }
        }
    }

    private void ListenForListenerSide()
    {
        _listenerSide = new TcpListener(IPAddress.Any, _settings.Port);
        _listenerSide.Start();
        Console.WriteLine($"[Proxy] Listening for LIS on port {_settings.Port}...");
        while (_running)
        {
            var lisClient = _listenerSide.AcceptTcpClient();
            Console.WriteLine("[Proxy] LIS connected.");
            var thread = new Thread(() => HandleListenerSide(lisClient));
            thread.Start();
        }
    }

    private void HandleListenerSide(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            _lastListenerStream = stream;
            while (_running && client.Connected)
            {
                if (_outboundBuffer.TryDequeue(out var message))
                {
                    var framed = Encoding.ASCII.GetBytes(message); // Already MLLP framed
                    stream.Write(framed, 0, framed.Length);
                    Console.WriteLine($"[Proxy] Forwarded to LIS: {message}");
                    _logger.Information("[Proxy] Forwarded to LIS: {Message}", message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Proxy] Error handling LIS connection: {ex.Message}");
            _logger.Error(ex, "[Proxy] Error occurred while handling LIS connection.");
        }
    }

    private void TrySendToListenerSide()
    {
        if (_lastListenerStream != null && _lastListenerStream.CanWrite)
        {
            while (_outboundBuffer.TryDequeue(out var message))
            {
                var framed = Encoding.ASCII.GetBytes(message); // Already MLLP framed
                _lastListenerStream.Write(framed, 0, framed.Length);
                Console.WriteLine($"[Proxy] Forwarded to LIS: {message}");
                _logger.Information("[Proxy] Forwarded to LIS: {Message}", message);
            }
        }
    }

    // Connects to LIS as a client, receives messages from LIS, and forwards to analyzer (listener side)
    private void ConnectToClientSide()
    {
        while (_running)
        {
            try
            {
                _clientSide = new TcpClient();
                _clientSide.Connect(_settings.ClientHost, _settings.ClientPort);
                _clientStream = _clientSide.GetStream();
                _clientConnected = true;
                Console.WriteLine($"[Proxy] Connected to LIS at {_settings.ClientHost}:{_settings.ClientPort}");
                // Receive from LIS and forward to analyzer
                while (_clientConnected && _clientSide.Connected)
                {
                    var buffer = new List<byte>();
                    int b = _clientStream.ReadByte();
                    if (b == -1) { _clientConnected = false; break; }
                    if (b == 0x0B)
                    {
                        buffer.Clear();
                        while (true)
                        {
                            int data = _clientStream.ReadByte();
                            if (data == -1) { _clientConnected = false; break; }
                            if (data == 0x1C)
                            {
                                int next = _clientStream.ReadByte();
                                if (next == 0x0D) break;
                                if (next != -1) buffer.Add((byte)data);
                                if (next != -1) buffer.Add((byte)next);
                                continue;
                            }
                            buffer.Add((byte)data);
                        }
                        string hl7 = Encoding.ASCII.GetString(buffer.ToArray());
                        if (Hl7Utils.IsAckMessage(hl7))
                        {
                            Console.WriteLine($"[Proxy] Intercepted ACK from LIS, not forwarding.");
                            _logger.Information("[Proxy] Intercepted ACK from LIS, not forwarding.");
                        }
                        else if (_lastListenerStream != null)
                        {
                            Console.WriteLine($"[Proxy] Received from LIS, forwarding to analyzer: {hl7}");
                            _logger.Information($"[Proxy] Received from LIS, forwarding to analyzer: {hl7}");
                            var framed = Encoding.ASCII.GetBytes(hl7); // Already MLLP framed
                            _lastListenerStream.Write(framed, 0, framed.Length);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _clientConnected = false;
                _logger.Error(ex, "Proxy LIS connection error");
                Console.WriteLine($"Proxy LIS connection error: {ex.Message}");
            }
            finally
            {
                _clientStream?.Close();
                _clientSide?.Close();
                _clientConnected = false;
            }
            Console.WriteLine("[Proxy] Reconnecting to LIS in 1 second...");
            Thread.Sleep(1000);
        }
    }
}

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
    private readonly ConcurrentQueue<string> _toLisBuffer = new();
    private readonly ConcurrentQueue<string> _toAnalyzerBuffer = new();
    private volatile bool _clientConnected = false;
    private volatile bool _running = true;
    private NetworkStream? _lastListenerStream; // For forwarding from client to listener
    private NetworkStream? _analyzerStream; // For forwarding to analyzer (proxy is client)
    private NetworkStream? _lisStream;      // For forwarding to LIS (proxy is listener)

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
        _logger.Information("Proxy listening for analyzer on port {Port}...", _settings.Port);
        var lisThread = new Thread(ConnectToClientSide);
        lisThread.Start();
        while (_running)
        {
            var analyzerClient = listener.AcceptTcpClient();
            _logger.Information("Analyzer connected.");
            var thread = new Thread(() => HandleListenerSideWithAck(analyzerClient));
            thread.Start();
        }
    }

    // Handles messages from the listener side (analyzer) and sends ACK immediately before forwarding
    private void HandleListenerSideWithAck(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            _lastListenerStream = stream;
            while (_running && client.Connected)
            {
                var buffer = new List<byte>();
                int b = stream.ReadByte();
                if (b == -1) break;
                if (b == 0x0B)
                {
                    buffer.Clear();
                    while (true)
                    {
                        int data = stream.ReadByte();
                        if (data == -1) break;
                        if (data == 0x1C)
                        {
                            int next = stream.ReadByte();
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
                        _logger.Information("[Proxy] Intercepted ACK, dropping.");
                        continue;
                    }
                    // Send ACK to sender immediately, before forwarding
                    string controlId = Hl7Utils.ExtractMSH10(hl7);
                    string ack = Hl7Utils.BuildAck(hl7, controlId, _settings);
                    var ackBytes = Hl7Utils.FrameMLLP(ack);
                    stream.Write(ackBytes, 0, ackBytes.Length);
                    _logger.Information("[Proxy] Sent ACK to analyzer: {Ack}", ack);
                    // Forward to LIS if connected, else buffer
                    if (_clientConnected && _clientStream != null)
                    {
                        var framed = Encoding.ASCII.GetBytes(hl7); // Already MLLP framed
                        _clientStream.Write(framed, 0, framed.Length);
                        _logger.Information("[Proxy] Forwarded to LIS: {Message}", hl7);
                    }
                    else
                    {
                        _toLisBuffer.Enqueue(hl7);
                        _logger.Information("[Proxy] LIS not connected, message buffered.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Proxy error handling listener side");
        }
    }

    // Proxy connects as client to analyzer, listens for LIS
    private void ClientToListenerProxy()
    {
        // Start listener for LIS
        var lisListener = new TcpListener(IPAddress.Any, _settings.Port);
        lisListener.Start();
        _logger.Information("[Proxy] Listening for LIS on port {Port}...", _settings.Port);
        // Connect to analyzer as client
        var analyzerThread = new Thread(ConnectToAnalyzerSide);
        analyzerThread.Start();
        while (_running)
        {
            var lisClient = lisListener.AcceptTcpClient();
            _logger.Information("[Proxy] LIS connected.");
            _lisStream = lisClient.GetStream();
            var thread = new Thread(() => HandleLISWithAckAndForward(lisClient));
            thread.Start();
            var flushThread = new Thread(FlushBufferToAnalyzer);
            flushThread.Start();
        }
    }

    // Connect to analyzer as client and handle messages
    private void ConnectToAnalyzerSide()
    {
        while (_running)
        {
            try
            {
                var analyzerClient = new TcpClient();
                analyzerClient.Connect(_settings.ClientHost, _settings.ClientPort);
                _analyzerStream = analyzerClient.GetStream();
                _logger.Information("[Proxy] Connected to analyzer at {Host}:{Port}", _settings.ClientHost, _settings.ClientPort);
                var thread = new Thread(() => HandleAnalyzerWithAckAndForward(analyzerClient));
                thread.Start();
                var flushThread = new Thread(FlushBufferToLIS);
                flushThread.Start();
                thread.Join();
                flushThread.Join();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[Proxy] Analyzer connection error");
            }
            finally
            {
                _analyzerStream = null;
            }
            _logger.Information("[Proxy] Reconnecting to analyzer in 1 second...");
            Thread.Sleep(1000);
        }
    }

    // Handle messages from LIS, send ACK, forward to analyzer
    private void HandleLISWithAckAndForward(TcpClient lisClient)
    {
        try
        {
            using var stream = lisClient.GetStream();
            while (_running && lisClient.Connected)
            {
                var buffer = new List<byte>();
                int b = stream.ReadByte();
                if (b == -1) break;
                if (b == 0x0B)
                {
                    buffer.Clear();
                    while (true)
                    {
                        int data = stream.ReadByte();
                        if (data == -1) break;
                        if (data == 0x1C)
                        {
                            int next = stream.ReadByte();
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
                        _logger.Information("[Proxy] Intercepted ACK from LIS, dropping.");
                        continue;
                    }
                    // Send ACK to LIS immediately
                    string controlId = Hl7Utils.ExtractMSH10(hl7);
                    string ack = Hl7Utils.BuildAck(hl7, controlId, _settings);
                    var ackBytes = Hl7Utils.FrameMLLP(ack);
                    stream.Write(ackBytes, 0, ackBytes.Length);
                    _logger.Information("[Proxy] Sent ACK to LIS: {Ack}", ack);
                    // Forward to analyzer if connected, else buffer
                    if (_analyzerStream != null)
                    {
                        var framed = Encoding.ASCII.GetBytes(hl7);
                        _analyzerStream.Write(framed, 0, framed.Length);
                        _logger.Information("[Proxy] Forwarded to analyzer: {Message}", hl7);
                    }
                    else
                    {
                        _toAnalyzerBuffer.Enqueue(hl7);
                        _logger.Information("[Proxy] Analyzer not connected, message buffered.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Proxy error handling LIS side");
        }
    }

    // Handle messages from analyzer, send ACK only if LIS is connected, forward to LIS
    private void HandleAnalyzerWithAckAndForward(TcpClient analyzerClient)
    {
        try
        {
            using var stream = analyzerClient.GetStream();
            while (_running && analyzerClient.Connected)
            {
                var buffer = new List<byte>();
                int b = stream.ReadByte();
                if (b == -1) break;
                if (b == 0x0B)
                {
                    buffer.Clear();
                    while (true)
                    {
                        int data = stream.ReadByte();
                        if (data == -1) break;
                        if (data == 0x1C)
                        {
                            int next = stream.ReadByte();
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
                        _logger.Information("[Proxy] Intercepted ACK from analyzer, dropping.");
                        continue;
                    }
                    // Forward to LIS if connected, else buffer
                    if (_lisStream != null)
                    {
                        var framed = Encoding.ASCII.GetBytes(hl7);
                        _lisStream.Write(framed, 0, framed.Length);
                        _logger.Information("[Proxy] Forwarded to LIS: {Message}", hl7);
                        // Send ACK to analyzer after forwarding
                        string controlId = Hl7Utils.ExtractMSH10(hl7);
                        string ack = Hl7Utils.BuildAck(hl7, controlId, _settings);
                        var ackBytes = Hl7Utils.FrameMLLP(ack);
                        stream.Write(ackBytes, 0, ackBytes.Length);
                        _logger.Information("[Proxy] Sent ACK to analyzer: {Ack}", ack);
                    }
                    else
                    {
                        _toLisBuffer.Enqueue(hl7);
                        _logger.Information("[Proxy] LIS not connected, message buffered. ACK will be sent after delivery.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Proxy error handling analyzer side");
        }
    }

    // Flush buffered messages to analyzer
    private void FlushBufferToAnalyzer()
    {
        while (_analyzerStream != null)
        {
            while (_toAnalyzerBuffer.TryDequeue(out var msg))
            {
                try
                {
                    var framed = Encoding.ASCII.GetBytes(msg);
                    _analyzerStream.Write(framed, 0, framed.Length);
                    _logger.Information("[Proxy] Forwarded buffered message to analyzer: {Message}", msg);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[Proxy] Error forwarding buffered message to analyzer, re-buffering");
                    _toAnalyzerBuffer.Enqueue(msg);
                    break;
                }
            }
            Thread.Sleep(100);
        }
    }

    // Flush buffered messages to LIS
    private void FlushBufferToLIS()
    {
        while (_lisStream != null)
        {
            while (_toLisBuffer.TryDequeue(out var msg))
            {
                try
                {
                    var framed = Encoding.ASCII.GetBytes(msg);
                    _lisStream.Write(framed, 0, framed.Length);
                    _logger.Information("[Proxy] Forwarded buffered message to LIS: {Message}", msg);
                    // Send ACK to analyzer if possible (find the original stream)
                    if (_analyzerStream != null)
                    {
                        string controlId = Hl7Utils.ExtractMSH10(msg);
                        string ack = Hl7Utils.BuildAck(msg, controlId, _settings);
                        var ackBytes = Hl7Utils.FrameMLLP(ack);
                        _analyzerStream.Write(ackBytes, 0, ackBytes.Length);
                        _logger.Information("[Proxy] Sent buffered ACK to analyzer: {Ack}", ack);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[Proxy] Error forwarding buffered message to LIS, re-buffering");
                    _toLisBuffer.Enqueue(msg);
                    break;
                }
            }
            Thread.Sleep(100);
        }
    }

    private void TrySendToListenerSide()
    {
        if (_lastListenerStream != null && _lastListenerStream.CanWrite)
        {
            while (_toAnalyzerBuffer.TryDequeue(out var message))
            {
                var framed = Encoding.ASCII.GetBytes(message); // Already MLLP framed
                _lastListenerStream.Write(framed, 0, framed.Length);
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
                _logger.Information("[Proxy] Connected to LIS at {Host}:{Port}", _settings.ClientHost, _settings.ClientPort);
                // Continuously flush the buffer while connected
                var bufferFlushThread = new Thread(() => FlushBufferToLIS());
                bufferFlushThread.Start();
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
                            _logger.Information("[Proxy] Intercepted ACK from LIS, dropping.");
                            continue;
                        }
                        // Send ACK to LIS immediately
                        string controlId = Hl7Utils.ExtractMSH10(hl7);
                        string ack = Hl7Utils.BuildAck(hl7, controlId, _settings);
                        var ackBytes = Hl7Utils.FrameMLLP(ack);
                        _clientStream.Write(ackBytes, 0, ackBytes.Length);
                        _logger.Information("[Proxy] Sent ACK to LIS: {Ack}", ack);
                        // Forward to analyzer
                        if (_lastListenerStream != null)
                        {
                            _logger.Information("[Proxy] Received from LIS, forwarding to analyzer: {Message}", hl7);
                            var framed = Encoding.ASCII.GetBytes(hl7); // Already MLLP framed
                            _lastListenerStream.Write(framed, 0, framed.Length);
                        }
                        else
                        {
                            _toAnalyzerBuffer.Enqueue(hl7);
                            _logger.Information("[Proxy] Analyzer not connected, message buffered.");
                        }
                    }
                }
                bufferFlushThread.Join();
            }
            catch (Exception ex)
            {
                _clientConnected = false;
                _logger.Error(ex, "Proxy LIS connection error");
            }
            finally
            {
                _clientStream?.Close();
                _clientSide?.Close();
                _clientConnected = false;
            }
            _logger.Information("[Proxy] Reconnecting to LIS in 1 second...");
            Thread.Sleep(1000);
        }
    }
}

# HL7ProxyBridge Usage Guide

HL7ProxyBridge is a flexible HL7 MLLP proxy, listener, and client for debugging, integration, and message routing scenarios. It can operate as a server, client, or proxy (in both directions), and always handles ACKs according to HL7 best practices.

## Features
- Proxy HL7 messages between two systems (Analyzer <-> LIS) with ACK interception
- Buffer and retry messages on connection loss
- Operate as listener, client, or proxy (listener->client or client->listener)
- Full logging with Serilog
- Configurable via `appsettings.json`

## Configuration
All configuration is done in `appsettings.json` under the `Hl7` section.

### Example
```json
{
  "Hl7": {
    "Port": 5100, // Listening port for the proxy or server
    "ClientHost": "127.0.0.1", // Host to connect to as a client (proxy or client mode)
    "ClientPort": 5200, // Port to connect to as a client
    "SendingApplication": "MySenderApp",
    "SendingFacility": "MySenderFacility",
    "ReceivingApplication": "MyReceiverApp",
    "ReceivingFacility": "MyReceiverFacility",
    "LogFilePath": "logs/hl7_{Date}.log",
    "AllowedEvents": [ "ADT^A01", "ORM^O01" ],
    "AckMode": "AA",
    "MessageDateTimeFormat": "yyyy-MM-dd HH:mm:ss",
    "DisconnectAfterAck": false,
    "IsServer": false,
    "Mode": "Proxy", // "Server", "Client", or "Proxy"
    "ProxyDirection": "ListenerToClient" // "ListenerToClient" (default) or "ClientToListener"
  }
}
```

### Key Properties
- **Mode**: 
  - `Server`: Listen for HL7 messages and send ACKs.
  - `Client`: Connect to a remote HL7 server and send/receive messages interactively.
  - `Proxy`: Bridge two systems, intercepting ACKs and forwarding messages.
- **ProxyDirection** (only for `Proxy` mode):
  - `ListenerToClient`: Proxy listens for analyzer, connects as client to LIS.
  - `ClientToListener`: Proxy connects as client to analyzer, listens for LIS.
- **Port**: The port to listen on (for server or proxy's listener side).
- **ClientHost/ClientPort**: The host/port to connect to as a client (for client or proxy's client side).
- **DisconnectAfterAck**: If true, disconnect after sending an ACK (for debugging or one-shot connections).

## Running the Application
1. Edit `appsettings.json` to match your environment and desired mode.
2. Build and run the application:
   ```sh
   dotnet run --project HL7ProxyBridge.csproj
   ```
3. The application will log to the console and to the file specified in `LogFilePath`.

## Proxy Mode Behavior
- **ACK Handling**: The proxy always intercepts and handles ACKs itself. ACKs from either side are not forwarded.
- **Message Forwarding**: All other HL7 messages are forwarded as-is (MLLP framing is preserved).
- **Buffering**: If the destination is unavailable, messages are buffered and retried when the connection is restored.

## Debugging & Troubleshooting
- All events are logged to the console and log file.
- Use `DisconnectAfterAck` for step-by-step debugging.
- Check the log file for detailed error and message flow information.

## License
MIT

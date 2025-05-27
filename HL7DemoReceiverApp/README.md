# HL7ProxyBridge

HL7ProxyBridge is a flexible, modern HL7 MLLP proxy, listener, and client for healthcare integration, debugging, and message routing. It is designed to help you bridge, monitor, and debug HL7 message flows between analyzers, LIS, and other HL7 systems.

## Features
- **Proxy Mode**: Intercepts and forwards HL7 messages between two systems, always handling ACKs locally and buffering messages for reliable delivery.
- **Listener Mode**: Acts as an HL7 MLLP server, receiving messages and sending ACKs.
- **Client Mode**: Connects to a remote HL7 MLLP server, allowing you to send and receive messages interactively.
- **Bidirectional Proxy**: Can operate as listener->client or client->listener, configurable via settings.
- **ACK Interception**: All ACKs are handled by the proxy and never forwarded, ensuring protocol compliance and easier debugging.
- **Buffering and Retry**: Messages are buffered and retried automatically if the destination is temporarily unavailable.
- **MLLP Framing**: All HL7 messages are handled with proper MLLP framing; only proxy-generated ACKs are re-framed as needed.
- **Configurable**: All behavior is controlled via `appsettings.json`.
- **Logging**: Full message and event logging using Serilog, with log file rotation.

## Quick Start
1. Edit `appsettings.json` to configure your mode (Server, Client, Proxy) and connection details.
2. Build and run the application:
   ```sh
   dotnet run --project HL7ProxyBridge.csproj
   ```
3. Monitor logs in the console and in the log file specified in your configuration.

## Configuration
See [USAGE.md](USAGE.md) for detailed configuration options and usage instructions.

## Typical Use Cases
- Debugging HL7 message flows between analyzers and LIS systems
- Bridging two HL7 systems with full ACK control
- Buffering and retrying HL7 messages during network outages
- Acting as a test HL7 server or client for development

## License
MIT

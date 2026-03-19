namespace HL7ProxyBridge
{
    /// <summary>
    /// HL7 application settings for all modes (server, client, proxy).
    /// </summary>
    public class Hl7Settings
    {
        public int Port { get; set; } = 5100;
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
        public string ClientHost { get; set; } = "127.0.0.1";
        public int ClientPort { get; set; } = 5200;
        public string Mode { get; set; } = "Server";
        public string ProxyDirection { get; set; } = "ListenerToClient";
    }
}

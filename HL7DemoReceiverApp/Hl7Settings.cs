namespace HL7DemoReceiverApp;

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

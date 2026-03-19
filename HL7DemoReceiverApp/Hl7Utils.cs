using System;
using System.Globalization;
using System.Text;

namespace HL7ProxyBridge;

public static class Hl7Utils
{
    public static byte[] FrameMLLP(string message)
    {
        var msgBytes = Encoding.ASCII.GetBytes(message);
        var framed = new byte[msgBytes.Length + 3];
        framed[0] = 0x0B;
        Buffer.BlockCopy(msgBytes, 0, framed, 1, msgBytes.Length);
        framed[framed.Length - 2] = 0x1C;
        framed[framed.Length - 1] = 0x0D;
        return framed;
    }

    public static string ExtractMSH10(string hl7)
    {
        var lines = hl7.Split('\r');
        var msh = Array.Find(lines, l => l.StartsWith("MSH"));
        if (msh == null) return string.Empty;
        var sep = msh[3];
        var fields = msh.Split(sep);
        return fields.Length > 9 ? fields[9] : string.Empty;
    }

    public static string BuildAck(string incoming, string controlId, Hl7Settings settings)
    {
        var msh = incoming.Split('\r').FirstOrDefault(l => l.StartsWith("MSH"));
        char sep = msh != null && msh.Length > 3 ? msh[3] : '|';
        string encodingChars = msh != null && msh.Length > 7 ? msh.Substring(4, 4) : "^~\\&";
        string sendingApp = msh?.Split(sep).ElementAtOrDefault(5) ?? settings.SendingApplication;
        string sendingFac = msh?.Split(sep).ElementAtOrDefault(6) ?? settings.SendingFacility;
        string receivingApp = msh?.Split(sep).ElementAtOrDefault(3) ?? settings.ReceivingApplication;
        string receivingFac = msh?.Split(sep).ElementAtOrDefault(4) ?? settings.ReceivingFacility;
        string timestamp = DateTime.Now.ToString(settings.MessageDateTimeFormat, CultureInfo.InvariantCulture);
        string ackMsg =
            $"MSH{sep}{encodingChars}{sep}{receivingApp}{sep}{receivingFac}{sep}{sendingApp}{sep}{sendingFac}{sep}{timestamp}{sep}{sep}ACK^R01{sep}{controlId}{sep}P{sep}2.3.1\r" +
            $"MSA{sep}{settings.AckMode}{sep}{controlId}\r";
        return ackMsg;
    }

    public static bool IsAckMessage(string hl7)
    {
        var lines = hl7.Split('\r');
        var msh = Array.Find(lines, l => l.StartsWith("MSH"));
        if (msh == null) return false;
        var sep = msh[3];
        var fields = msh.Split(sep);
        return fields.Length > 8 && fields[8].StartsWith("ACK");
    }
}

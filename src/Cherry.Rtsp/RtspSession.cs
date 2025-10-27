using System;

namespace Cherry.Rtsp
{
    public class RtspSession
    {
        public string SessionId { get; set; }
        public string State { get; set; } = "INIT"; // INIT, READY, PLAYING, RECORDING, PAUSED
        public string Uri { get; set; }
        public string Transport { get; set; }
        public int ClientRtpPort { get; set; }
        public int ClientRtcpPort { get; set; }
        public int ServerRtpPort { get; set; }
        public int ServerRtcpPort { get; set; }
        // Add more as needed, like media info
    }
}
using System;
using System.Collections.Generic;

namespace Cherry.Rtsp
{
    public abstract class RtspMessage
    {
        public Dictionary<string, string> Headers { get; } = new();
        public string Body { get; set; } = string.Empty;

        public abstract string ToString();
    }

    public class RtspRequest : RtspMessage
    {
        public string Method { get; set; }
        public string Uri { get; set; }
        public string Version { get; set; } = "RTSP/1.0";

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{Method} {Uri} {Version}");
            foreach (var header in Headers)
            {
                sb.AppendLine($"{header.Key}: {header.Value}");
            }
            sb.AppendLine();
            sb.Append(Body);
            return sb.ToString();
        }
    }

    public class RtspResponse : RtspMessage
    {
        public string Version { get; set; } = "RTSP/1.0";
        public int StatusCode { get; set; }
        public string ReasonPhrase { get; set; }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{Version} {StatusCode} {ReasonPhrase}");
            foreach (var header in Headers)
            {
                sb.AppendLine($"{header.Key}: {header.Value}");
            }
            sb.AppendLine();
            sb.Append(Body);
            return sb.ToString();
        }
    }
}
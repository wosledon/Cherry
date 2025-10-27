using System;
using System.IO;

namespace Cherry.Rtsp
{
    public static class RtspParser
    {
        public static RtspMessage Parse(string messageText)
        {
            using var reader = new StringReader(messageText);
            string line = reader.ReadLine();
            if (line == null) throw new ArgumentException("Invalid RTSP message");

            if (line.StartsWith("RTSP/"))
            {
                // Response
                var parts = line.Split(' ');
                if (parts.Length < 3) throw new ArgumentException("Invalid response line");
                var response = new RtspResponse
                {
                    Version = parts[0],
                    StatusCode = int.Parse(parts[1]),
                    ReasonPhrase = string.Join(" ", parts.Skip(2))
                };
                ParseHeaders(reader, response);
                return response;
            }
            else
            {
                // Request
                var parts = line.Split(' ');
                if (parts.Length < 3) throw new ArgumentException("Invalid request line");
                var request = new RtspRequest
                {
                    Method = parts[0],
                    Uri = parts[1],
                    Version = parts[2]
                };
                ParseHeaders(reader, request);
                return request;
            }
        }

        private static void ParseHeaders(StringReader reader, RtspMessage message)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) break;
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    message.Headers[key] = value;
                }
            }
            // Body is the rest
            message.Body = reader.ReadToEnd();
        }
    }
}
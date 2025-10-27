using System;
using System.Net.Sockets;
using System.Text;
using Cherry.Rtsp;

namespace Cherry.Rtsp.Client
{
    public class RtspClient
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private int _cseq = 1;
        private string? _sessionId;
        private IAuthentication? _authentication;

        public void Connect(string host, int port = 554)
        {
            _client = new TcpClient(host, port);
            _stream = _client.GetStream();
        }

        public void SetAuthentication(IAuthentication auth)
        {
            _authentication = auth;
        }

        public RtspResponse SendRequest(RtspRequest request)
        {
            if (_stream == null) throw new InvalidOperationException("Client not connected");

            request.Headers["CSeq"] = _cseq++.ToString();
            if (!string.IsNullOrEmpty(_sessionId))
            {
                request.Headers["Session"] = _sessionId;
            }

            // Apply authentication if set
            _authentication?.ApplyToRequest(request);

            var requestText = request.ToString();
            var bytes = Encoding.UTF8.GetBytes(requestText);
            _stream.Write(bytes, 0, bytes.Length);

            // Read response
            var buffer = new byte[4096];
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            string responseText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var response = (RtspResponse)RtspParser.Parse(responseText);

            if (response.Headers.ContainsKey("Session"))
            {
                _sessionId = response.Headers["Session"];
            }

            // Check if authentication is needed
            if (_authentication != null && !_authentication.ValidateResponse(response))
            {
                // Retry with authentication
                _cseq--; // Reuse CSeq
                return SendRequest(request);
            }

            return response;
        }

        public RtspResponse Options(string uri)
        {
            var request = new RtspRequest { Method = "OPTIONS", Uri = uri };
            return SendRequest(request);
        }

        public RtspResponse Describe(string uri)
        {
            var request = new RtspRequest { Method = "DESCRIBE", Uri = uri };
            request.Headers["Accept"] = "application/sdp";
            return SendRequest(request);
        }

        public RtspResponse Setup(string uri, string transport)
        {
            var request = new RtspRequest { Method = "SETUP", Uri = uri };
            request.Headers["Transport"] = transport;
            return SendRequest(request);
        }

        public RtspResponse Play(string uri)
        {
            var request = new RtspRequest { Method = "PLAY", Uri = uri };
            return SendRequest(request);
        }

        public RtspResponse Pause(string uri)
        {
            var request = new RtspRequest { Method = "PAUSE", Uri = uri };
            return SendRequest(request);
        }

        public RtspResponse Teardown(string uri)
        {
            var request = new RtspRequest { Method = "TEARDOWN", Uri = uri };
            return SendRequest(request);
        }

        public void Disconnect()
        {
            _stream?.Close();
            _client?.Close();
        }
    }
}
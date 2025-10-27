using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using Cherry.Rtsp;

namespace Cherry.Rtsp.Server
{
    public class RtspServer
    {
        private TcpListener? _listener;
        private bool _running;
        private Dictionary<string, RtspSession> _sessions = new();
        private int _nextSessionId = 1;
        private Dictionary<string, string> _users = new() { { "admin", "password" } }; // Simple user store
        private string _realm = "CherryRTSP";
        private string _nonce;

        public RtspServer()
        {
            _nonce = GenerateNonce();
        }

        private string GenerateNonce()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }

        public void Start(int port = 554)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            Console.WriteLine($"RTSP Server started on port {port}");
            while (_running)
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
        }

        private void HandleClient(object obj)
        {
            var client = (TcpClient)obj;
            string? sessionId = null;
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            while (client.Connected)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                string messageText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var message = RtspParser.Parse(messageText);
                if (message is RtspRequest request)
                {
                    var response = HandleRequest(request, ref sessionId);
                    var responseText = response.ToString();
                    var responseBytes = Encoding.UTF8.GetBytes(responseText);
                    stream.Write(responseBytes, 0, responseBytes.Length);
                }
            }
            if (!string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId))
            {
                _sessions.Remove(sessionId);
            }
            client.Close();
        }

        private RtspResponse HandleRequest(RtspRequest request, ref string? sessionId)
        {
            // Check authentication
            if (!Authenticate(request))
            {
                var authResponse = new RtspResponse { StatusCode = 401, ReasonPhrase = "Unauthorized" };
                authResponse.Headers["CSeq"] = request.Headers.GetValueOrDefault("CSeq", "1");
                authResponse.Headers["WWW-Authenticate"] = $"Digest realm=\"{_realm}\", nonce=\"{_nonce}\", qop=\"auth\"";
                return authResponse;
            }

            var response = new RtspResponse { StatusCode = 200, ReasonPhrase = "OK" };
            response.Headers["CSeq"] = request.Headers.GetValueOrDefault("CSeq", "1");

            if (request.Headers.ContainsKey("Session"))
            {
                sessionId = request.Headers["Session"];
            }

            switch (request.Method)
            {
                case "OPTIONS":
                    response.Headers["Public"] = "DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE, GET_PARAMETER, SET_PARAMETER";
                    break;
                case "DESCRIBE":
                    response.Body = GenerateSdp(request.Uri);
                    response.Headers["Content-Type"] = "application/sdp";
                    response.Headers["Content-Length"] = response.Body.Length.ToString();
                    break;
                case "SETUP":
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        sessionId = _nextSessionId++.ToString();
                        _sessions[sessionId] = new RtspSession { SessionId = sessionId, Uri = request.Uri, State = "READY" };
                    }
                    var session = _sessions[sessionId];
                    session.Transport = request.Headers.GetValueOrDefault("Transport", "RTP/AVP/TCP");
                    response.Headers["Session"] = sessionId;
                    response.Headers["Transport"] = session.Transport;
                    break;
                case "PLAY":
                    if (!string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId))
                    {
                        _sessions[sessionId].State = "PLAYING";
                        response.Headers["Session"] = sessionId;
                        response.Headers["RTP-Info"] = "url=" + request.Uri + ";seq=1;rtptime=0";
                    }
                    else
                    {
                        response.StatusCode = 454;
                        response.ReasonPhrase = "Session Not Found";
                    }
                    break;
                case "PAUSE":
                    if (!string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId))
                    {
                        _sessions[sessionId].State = "READY";
                        response.Headers["Session"] = sessionId;
                    }
                    else
                    {
                        response.StatusCode = 454;
                        response.ReasonPhrase = "Session Not Found";
                    }
                    break;
                case "TEARDOWN":
                    if (!string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId))
                    {
                        _sessions.Remove(sessionId);
                        response.Headers["Session"] = sessionId;
                    }
                    else
                    {
                        response.StatusCode = 454;
                        response.ReasonPhrase = "Session Not Found";
                    }
                    break;
                case "GET_PARAMETER":
                    // Simple implementation
                    break;
                case "SET_PARAMETER":
                    // Simple implementation
                    break;
                default:
                    response.StatusCode = 501;
                    response.ReasonPhrase = "Not Implemented";
                    break;
            }
            return response;
        }

        private string GenerateSdp(string uri)
        {
            return $"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Test Stream\r\nt=0 0\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\na=control:{uri}\r\n";
        }

        private bool Authenticate(RtspRequest request)
        {
            if (!request.Headers.ContainsKey("Authorization"))
            {
                return false;
            }

            string authHeader = request.Headers["Authorization"];

            if (authHeader.StartsWith("Basic "))
            {
                return ValidateBasicAuth(authHeader);
            }
            else if (authHeader.StartsWith("Digest "))
            {
                return ValidateDigestAuth(authHeader, request.Method, request.Uri);
            }

            return false;
        }

        private bool ValidateBasicAuth(string authHeader)
        {
            string encoded = authHeader.Replace("Basic ", "");
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = decoded.Split(':');
            if (parts.Length == 2)
            {
                string username = parts[0];
                string password = parts[1];
                return _users.ContainsKey(username) && _users[username] == password;
            }
            return false;
        }

        private bool ValidateDigestAuth(string authHeader, string method, string uri)
        {
            // Parse digest auth header
            var parts = authHeader.Replace("Digest ", "").Split(',');
            string? username = null;
            string? realm = null;
            string? nonce = null;
            string? uriParam = null;
            string? response = null;
            string? qop = null;
            string? nc = null;
            string? cnonce = null;

            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    var value = kv[1].Trim().Trim('"');
                    switch (key)
                    {
                        case "username": username = value; break;
                        case "realm": realm = value; break;
                        case "nonce": nonce = value; break;
                        case "uri": uriParam = value; break;
                        case "response": response = value; break;
                        case "qop": qop = value; break;
                        case "nc": nc = value; break;
                        case "cnonce": cnonce = value; break;
                    }
                }
            }

            if (username == null || !_users.ContainsKey(username) || nonce != _nonce || realm != _realm)
            {
                return false;
            }

            string password = _users[username];
            string ha1 = ComputeMd5($"{username}:{realm}:{password}");
            string ha2 = ComputeMd5($"{method}:{uriParam}");
            string expectedResponse = ComputeMd5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");

            return response == expectedResponse;
        }

        private string ComputeMd5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}
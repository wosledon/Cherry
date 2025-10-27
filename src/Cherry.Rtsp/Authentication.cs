using System;
using System.Security.Cryptography;
using System.Text;

namespace Cherry.Rtsp
{
    public interface IAuthentication
    {
        void ApplyToRequest(RtspRequest request);
        bool ValidateResponse(RtspResponse response);
    }

    public abstract class AuthenticationBase : IAuthentication
    {
        protected string Username { get; }
        protected string Password { get; }

        protected AuthenticationBase(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public abstract void ApplyToRequest(RtspRequest request);
        public abstract bool ValidateResponse(RtspResponse response);
    }

    public class BasicAuthentication : AuthenticationBase
    {
        public BasicAuthentication(string username, string password) : base(username, password) { }

        public override void ApplyToRequest(RtspRequest request)
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
            request.Headers["Authorization"] = $"Basic {credentials}";
        }

        public override bool ValidateResponse(RtspResponse response)
        {
            // Basic auth doesn't require response validation
            return response.StatusCode != 401;
        }
    }

    public class DigestAuthentication : AuthenticationBase
    {
        private string _realm;
        private string _nonce;
        private string _qop = "auth";
        private string _nc = "00000001";
        private string _cnonce;

        public DigestAuthentication(string username, string password) : base(username, password)
        {
            _cnonce = GenerateCNonce();
        }

        public override void ApplyToRequest(RtspRequest request)
        {
            if (string.IsNullOrEmpty(_realm) || string.IsNullOrEmpty(_nonce))
            {
                // If no challenge received, can't authenticate
                return;
            }

            string method = request.Method;
            string uri = request.Uri;
            string ha1 = ComputeMd5($"{Username}:{_realm}:{Password}");
            string ha2 = ComputeMd5($"{method}:{uri}");
            string response = ComputeMd5($"{ha1}:{_nonce}:{_nc}:{_cnonce}:{_qop}:{ha2}");

            request.Headers["Authorization"] = $"Digest username=\"{Username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{response}\", qop={_qop}, nc={_nc}, cnonce=\"{_cnonce}\"";
        }

        public override bool ValidateResponse(RtspResponse response)
        {
            if (response.StatusCode == 401 && response.Headers.ContainsKey("WWW-Authenticate"))
            {
                ParseChallenge(response.Headers["WWW-Authenticate"]);
                return false; // Need to retry with auth
            }
            return response.StatusCode != 401;
        }

        private void ParseChallenge(string challenge)
        {
            // Parse WWW-Authenticate header
            // Example: Digest realm="testrealm@host.com", qop="auth,auth-int", nonce="dcd98b7102dd2f0e8b11d0f600bfb0c093", opaque="5ccc069c403ebaf9f0171e9517f40e41"
            var parts = challenge.Replace("Digest ", "").Split(',');
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    var value = kv[1].Trim().Trim('"');
                    switch (key)
                    {
                        case "realm":
                            _realm = value;
                            break;
                        case "nonce":
                            _nonce = value;
                            break;
                        case "qop":
                            _qop = value;
                            break;
                    }
                }
            }
        }

        private string ComputeMd5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        private string GenerateCNonce()
        {
            return Guid.NewGuid().ToString().Replace("-", "");
        }
    }
}
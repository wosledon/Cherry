# RTSP Authentication Implementation

This implementation provides multiple authentication methods for RTSP (Real Time Streaming Protocol):

## Supported Authentication Methods

### 1. Basic Authentication
- Uses Base64 encoded username:password
- Simple but less secure (credentials sent in plain text, base64 is easily decoded)
- Suitable for development/testing environments

### 2. Digest Authentication
- Uses MD5 hashing for secure credential transmission
- More secure than Basic auth as passwords are not sent in plain text
- Follows RFC 2617 standard

## Usage

### Client Side

```csharp
using Cherry.Rtsp.Client;

// Basic Authentication
var client = new RtspClient();
client.SetAuthentication(new BasicAuthentication("username", "password"));
client.Connect("server", 554);

// Digest Authentication
var client = new RtspClient();
client.SetAuthentication(new DigestAuthentication("username", "password"));
client.Connect("server", 554);

// Send requests as usual
var response = client.Options("rtsp://server/stream");
```

### Server Side

The server automatically handles authentication for all requests. It supports both Basic and Digest authentication.

Default credentials (for testing):
- Username: `admin`
- Password: `password`

To customize credentials, modify the `_users` dictionary in `RtspServer.cs`.

## How It Works

### Client Authentication Flow
1. Client sends request without authentication
2. Server responds with 401 Unauthorized and WWW-Authenticate header
3. Client parses the challenge and resends request with Authorization header
4. Server validates credentials and processes the request

### Server Authentication Flow
1. Server checks for Authorization header in incoming requests
2. If missing, returns 401 with authentication challenge
3. If present, validates credentials using appropriate method
4. Processes request if authentication succeeds

## Security Notes

- Basic authentication should only be used over secure connections (HTTPS/TLS)
- Digest authentication provides better security for password protection
- Consider implementing additional security measures for production use
- The current implementation uses a simple in-memory user store for demonstration

## Testing

Run the included test application:

```bash
cd samples
dotnet run --project AuthTest.csproj
```

This will test both Basic and Digest authentication against a local RTSP server.
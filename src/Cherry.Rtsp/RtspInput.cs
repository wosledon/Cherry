using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Cherry.Media;

namespace Cherry.Rtsp
{
    /// <summary>
    /// RTSP输入实现
    /// </summary>
    public class RtspInput : IMediaInput
    {
        public event EventHandler<MediaFrame>? FrameReceived;
        public event EventHandler<MediaStream>? StreamInfoReceived;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private UdpClient? _rtpClient;
        private UdpClient? _rtcpClient;
        private bool _isRunning;
        private int _cseq = 1;
        private string? _sessionId;
        private readonly Dictionary<int, RtpStream> _rtpStreams = new();

        public bool IsRunning => _isRunning;

        public async Task StartAsync(string url)
        {
            if (_isRunning) return;

            var uri = new Uri(url);
            string host = uri.Host;
            int port = uri.Port == -1 ? 554 : uri.Port;

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();

            _isRunning = true;

            // RTSP握手过程
            await PerformRtspHandshake(url);

            // 开始接收RTP数据
            _ = Task.Run(ReceiveRtpLoop);
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _rtpClient?.Close();
            _rtcpClient?.Close();
            _stream?.Close();
            _client?.Close();
        }

        private async Task PerformRtspHandshake(string url)
        {
            if (_stream == null) return;

            // OPTIONS
            var optionsRequest = CreateRtspRequest("OPTIONS", url);
            await SendRtspRequest(optionsRequest);
            var response = await ReceiveRtspResponse();
            ParsePublicMethods(response);

            // DESCRIBE
            var describeRequest = CreateRtspRequest("DESCRIBE", url);
            describeRequest.Headers["Accept"] = "application/sdp";
            await SendRtspRequest(describeRequest);
            response = await ReceiveRtspResponse();
            var sdp = ParseSdp(response.Body);

            // SETUP
            var setupRequest = CreateRtspRequest("SETUP", $"{url}/track1");
            setupRequest.Headers["Transport"] = "RTP/AVP/TCP;unicast;client_port=1234-1235";
            await SendRtspRequest(setupRequest);
            response = await ReceiveRtspResponse();
            ParseTransport(response);

            // PLAY
            var playRequest = CreateRtspRequest("PLAY", url);
            await SendRtspRequest(playRequest);
            response = await ReceiveRtspResponse();

            // 通知流信息
            var stream = new MediaStream
            {
                StreamId = "rtsp_stream",
                VideoCodec = CodecType.H264, // 从SDP解析
                AudioCodec = CodecType.AAC
            };
            StreamInfoReceived?.Invoke(this, stream);
        }

        private async Task SendRtspRequest(RtspRequest request)
        {
            if (_stream == null) return;

            var data = Encoding.UTF8.GetBytes(request.ToString());
            await _stream.WriteAsync(data);
        }

        private async Task<RtspResponse> ReceiveRtspResponse()
        {
            if (_stream == null) throw new InvalidOperationException();

            var buffer = new byte[4096];
            int bytesRead = await _stream.ReadAsync(buffer);
            var responseText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            return (RtspResponse)RtspParser.Parse(responseText);
        }

        private async Task ReceiveRtpLoop()
        {
            // 对于TCP传输，RTP数据通过RTSP连接接收
            if (_stream == null) return;

            var buffer = new byte[4096];

            while (_isRunning)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    // 解析RTP over RTSP数据
                    ProcessRtpData(buffer.AsSpan(0, bytesRead));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTSP RTP receive error: {ex.Message}");
                    break;
                }
            }
        }

        private void ProcessRtpData(ReadOnlySpan<byte> data)
        {
            // 解析RTP包
            if (data.Length < 12) return;

            // RTP头格式
            byte version = (byte)((data[0] >> 6) & 0x03);
            if (version != 2) return; // RTP version 2

            bool hasExtension = ((data[0] >> 4) & 0x01) == 1;
            byte csrcCount = (byte)(data[0] & 0x0F);
            bool hasPadding = ((data[1] >> 7) & 0x01) == 1;

            byte payloadType = (byte)(data[1] & 0x7F);
            ushort sequenceNumber = (ushort)((data[2] << 8) | data[3]);
            uint timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
            uint ssrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);

            int headerLength = 12 + (csrcCount * 4);
            if (hasExtension)
            {
                // 处理扩展头
                if (data.Length < headerLength + 4) return;
                ushort extensionLength = (ushort)((data[headerLength + 2] << 8) | data[headerLength + 3]);
                headerLength += 4 + (extensionLength * 4);
            }

            var payload = data.Slice(headerLength);
            if (hasPadding)
            {
                byte paddingLength = payload[^1];
                payload = payload.Slice(0, payload.Length - paddingLength);
            }

            // 根据payload type创建媒体帧
            var frame = new MediaFrame
            {
                Data = payload.ToArray(),
                Timestamp = timestamp
            };

            if (payloadType >= 96) // 动态payload type
            {
                // 假设H264
                frame.Type = MediaFrameType.Video;
                frame.Codec = CodecType.H264;
                // 检查是否为关键帧
                if (payload.Length > 4)
                {
                    uint naluType = (uint)(payload[4] & 0x1F);
                    frame.IsKeyFrame = naluType == 5; // IDR slice
                }
            }
            else if (payloadType >= 0 && payloadType <= 95)
            {
                // 标准payload type
                switch (payloadType)
                {
                    case 0: // PCMU
                    case 8: // PCMA
                        frame.Type = MediaFrameType.Audio;
                        frame.Codec = CodecType.Unknown;
                        break;
                    default:
                        frame.Type = MediaFrameType.Video;
                        frame.Codec = CodecType.H264;
                        break;
                }
            }

            FrameReceived?.Invoke(this, frame);
        }

        private RtspRequest CreateRtspRequest(string method, string uri)
        {
            return new RtspRequest
            {
                Method = method,
                Uri = uri,
                Headers =
                {
                    ["CSeq"] = _cseq++.ToString(),
                    ["User-Agent"] = "CherryRTSP/1.0"
                }
            };
        }

        private void ParsePublicMethods(RtspResponse response)
        {
            // 解析Public头
        }

        private string ParseSdp(string sdp)
        {
            // 解析SDP
            return sdp;
        }

        private void ParseTransport(RtspResponse response)
        {
            // 解析Transport头
            if (response.Headers.ContainsKey("Transport"))
            {
                var transport = response.Headers["Transport"];
                // 解析服务器端口等信息
            }

            if (response.Headers.ContainsKey("Session"))
            {
                _sessionId = response.Headers["Session"];
            }
        }

        private class RtpStream
        {
            public int Ssrc { get; set; }
            public CodecType Codec { get; set; }
        }
    }
}
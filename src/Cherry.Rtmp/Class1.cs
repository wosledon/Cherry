using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cherry.Media;

namespace Cherry.Rtmp
{
    /// <summary>
    /// RTMP消息类型
    /// </summary>
    public enum RtmpMessageType : byte
    {
        SetChunkSize = 1,
        Abort = 2,
        Acknowledgement = 3,
        UserControl = 4,
        WindowAcknowledgementSize = 5,
        SetPeerBandwidth = 6,
        Audio = 8,
        Video = 9,
        Amf3Data = 15,
        Amf3SharedObject = 16,
        Amf3Command = 17,
        Amf0Data = 18,
        Amf0SharedObject = 19,
        Amf0Command = 20,
        Aggregate = 22
    }

    /// <summary>
    /// RTMP块头
    /// </summary>
    public class RtmpChunkHeader
    {
        public byte Format { get; set; }
        public uint ChunkStreamId { get; set; }
        public uint Timestamp { get; set; }
        public uint MessageLength { get; set; }
        public RtmpMessageType MessageType { get; set; }
        public uint MessageStreamId { get; set; }
    }

    /// <summary>
    /// RTMP输入实现
    /// </summary>
    public class RtmpInput : IMediaInput
    {
        public event EventHandler<MediaFrame>? FrameReceived;
        public event EventHandler<MediaStream>? StreamInfoReceived;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _isRunning;
    private readonly RtmpChunkDecoder _decoder = new RtmpChunkDecoder();
    private readonly RtmpChunkEncoder _encoder = new RtmpChunkEncoder();

        public bool IsRunning => _isRunning;

        public async Task StartAsync(string url)
        {
            if (_isRunning) return;

            // 解析RTMP URL
            var uri = new Uri(url);
            string host = uri.Host;
            int port = uri.Port == -1 ? 1935 : uri.Port;

            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();

            _isRunning = true;

            // RTMP握手
            await PerformHandshake();

            // 开始接收数据
            _ = Task.Run(ReceiveLoop);
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _stream?.Close();
            _client?.Close();
        }

        private async Task PerformHandshake()
        {
            if (_stream == null) return;

            // RTMP握手过程
            var c0c1 = new byte[1537];
            c0c1[0] = 3; // RTMP version
            // 填充随机数据
            var random = new Random();
            for (int i = 1; i < c0c1.Length; i++)
            {
                c0c1[i] = (byte)random.Next(256);
            }

            await _stream.WriteAsync(c0c1);

            // 读取S0S1S2
            var s0s1s2 = new byte[3073];
            await _stream.ReadAsync(s0s1s2);

            // 发送C2
            var c2 = new byte[1536];
            Array.Copy(s0s1s2, 1, c2, 0, 1536);
            await _stream.WriteAsync(c2);
        }

        private async Task ReceiveLoop()
        {
            if (_stream == null) return;

            var buffer = new byte[8192];

            // 订阅解码器事件以便在解析到消息时输出通信内容
            _decoder.MessageDecoded += (m) =>
            {
                Console.WriteLine($"[RTMP][IN] type={m.Header.MessageType} stream={m.Header.MessageStreamId} ts={m.Header.Timestamp} len={m.Payload.Length}");
            };

            // 订阅编码器事件以便在发送 chunk 前输出
            _encoder.ChunkEncoded += (b) =>
            {
                Console.WriteLine($"[RTMP][OUT] chunk len={b.Length}");
            };

            while (_isRunning)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (bytesRead == 0) break;

                    var span = new ReadOnlySpan<byte>(buffer, 0, bytesRead);
                    foreach (var msg in _decoder.Feed(span))
                    {
                        // 处理控制消息（例如 SetChunkSize）
                        if (msg.Header.MessageType == RtmpMessageType.SetChunkSize && msg.Payload.Length >= 4)
                        {
                            uint newSize = (uint)(msg.Payload[0] << 24 | msg.Payload[1] << 16 | msg.Payload[2] << 8 | msg.Payload[3]);
                            _decoder.ChunkSize = (int)newSize;
                            _encoder.ChunkSize = (int)newSize; // 同步更新发送端
                            // 同步更新本地记录（若需要用于其他逻辑）
                        }

                        ProcessMessage(msg.Header, msg.Payload);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTMP receive error: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// 通过编码器将一条消息发送到网络（异步写入 stream）并输出发送信息
        /// </summary>
        public async System.Threading.Tasks.Task SendMessageAsync(RtmpMessage msg)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            foreach (var chunk in _encoder.Encode(msg))
            {
                await _stream.WriteAsync(chunk.AsMemory(0, chunk.Length));
            }
        }

        private void ProcessMessage(RtmpChunkHeader header, byte[] messageData)
        {
            switch (header.MessageType)
            {
                case RtmpMessageType.Video:
                    ProcessVideoData(messageData, header.Timestamp);
                    break;
                case RtmpMessageType.Audio:
                    ProcessAudioData(messageData, header.Timestamp);
                    break;
                case RtmpMessageType.Amf0Data:
                case RtmpMessageType.Amf3Data:
                    ProcessMetadata(messageData);
                    break;
            }
        }

        private void ProcessVideoData(byte[] data, uint timestamp)
        {
            if (data.Length < 1) return;

            var frame = new MediaFrame
            {
                Type = MediaFrameType.Video,
                Timestamp = timestamp,
                Data = data.AsSpan(1).ToArray() // 跳过第一个字节（帧类型）
            };

            // 解析帧类型
            byte frameInfo = data[0];
            frame.IsKeyFrame = (frameInfo >> 4) == 1;

            // 解析编解码器
            byte codecId = (byte)(frameInfo & 0x0F);
            frame.Codec = codecId switch
            {
                7 => CodecType.H264,
                12 => CodecType.H265,
                _ => CodecType.Unknown
            };

            FrameReceived?.Invoke(this, frame);
        }

        private void ProcessAudioData(byte[] data, uint timestamp)
        {
            if (data.Length < 1) return;

            var frame = new MediaFrame
            {
                Type = MediaFrameType.Audio,
                Timestamp = timestamp,
                Data = data.AsSpan(1).ToArray() // 跳过第一个字节（音频格式）
            };

            // 解析音频格式
            byte audioInfo = data[0];
            byte soundFormat = (byte)(audioInfo >> 4);
            frame.Codec = soundFormat switch
            {
                10 => CodecType.AAC,
                2 => CodecType.MP3,
                _ => CodecType.Unknown
            };

            FrameReceived?.Invoke(this, frame);
        }

        private void ProcessMetadata(byte[] data)
        {
            // 处理元数据
            var stream = new MediaStream();
            // 解析AMF数据获取流信息
            StreamInfoReceived?.Invoke(this, stream);
        }

        private uint ReadUInt24(ReadOnlySpan<byte> data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
        }

        private uint ReadUInt32LittleEndian(ReadOnlySpan<byte> data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }
}

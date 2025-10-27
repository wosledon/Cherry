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
        private readonly Dictionary<uint, RtmpChunkHeader> _chunkHeaders = new();
        private uint _chunkSize = 128;

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

            var buffer = new byte[4096];
            var chunkBuffer = new Dictionary<uint, List<byte>>();

            while (_isRunning)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    // 处理RTMP数据
                    ProcessRtmpData(buffer.AsSpan(0, bytesRead), chunkBuffer);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTMP receive error: {ex.Message}");
                    break;
                }
            }
        }

        private void ProcessRtmpData(ReadOnlySpan<byte> data, Dictionary<uint, List<byte>> chunkBuffer)
        {
            int offset = 0;

            while (offset < data.Length)
            {
                var header = ParseChunkHeader(data, ref offset);
                if (header == null) break;

                // 获取或创建块缓冲区
                if (!chunkBuffer.ContainsKey(header.ChunkStreamId))
                {
                    chunkBuffer[header.ChunkStreamId] = new List<byte>();
                }

                var buffer = chunkBuffer[header.ChunkStreamId];

                // 读取块数据
                int chunkDataSize = Math.Min((int)_chunkSize, (int)header.MessageLength - buffer.Count);
                if (offset + chunkDataSize > data.Length) break;

                buffer.AddRange(data.Slice(offset, chunkDataSize));
                offset += chunkDataSize;

                // 检查是否收到完整消息
                if (buffer.Count >= header.MessageLength)
                {
                    ProcessMessage(header, buffer.ToArray());
                    buffer.Clear();
                }
            }
        }

        private RtmpChunkHeader? ParseChunkHeader(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 1 > data.Length) return null;

            byte firstByte = data[offset++];
            byte format = (byte)(firstByte >> 6);
            uint chunkStreamId = (uint)(firstByte & 0x3F);

            if (chunkStreamId == 0)
            {
                if (offset + 1 > data.Length) return null;
                chunkStreamId = (uint)(data[offset++] + 64);
            }
            else if (chunkStreamId == 1)
            {
                if (offset + 2 > data.Length) return null;
                chunkStreamId = (uint)(data[offset] + (data[offset + 1] << 8) + 64);
                offset += 2;
            }

            var header = new RtmpChunkHeader { Format = format, ChunkStreamId = chunkStreamId };

            // 根据格式读取其余头信息
            switch (format)
            {
                case 0: // 11字节头
                    if (offset + 11 > data.Length) return null;
                    header.Timestamp = ReadUInt24(data, offset);
                    header.MessageLength = ReadUInt24(data, offset + 3);
                    header.MessageType = (RtmpMessageType)data[offset + 6];
                    header.MessageStreamId = ReadUInt32LittleEndian(data, offset + 7);
                    offset += 11;
                    break;
                case 1: // 7字节头
                    if (offset + 7 > data.Length) return null;
                    header.Timestamp = ReadUInt24(data, offset);
                    header.MessageLength = ReadUInt24(data, offset + 3);
                    header.MessageType = (RtmpMessageType)data[offset + 6];
                    offset += 7;
                    break;
                case 2: // 3字节头
                    if (offset + 3 > data.Length) return null;
                    header.Timestamp = ReadUInt24(data, offset);
                    offset += 3;
                    break;
                case 3: // 0字节头
                    // 使用之前的头信息
                    if (_chunkHeaders.ContainsKey(chunkStreamId))
                    {
                        var prevHeader = _chunkHeaders[chunkStreamId];
                        header.Timestamp = prevHeader.Timestamp;
                        header.MessageLength = prevHeader.MessageLength;
                        header.MessageType = prevHeader.MessageType;
                        header.MessageStreamId = prevHeader.MessageStreamId;
                    }
                    break;
            }

            _chunkHeaders[chunkStreamId] = header;
            return header;
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

using System;
using System.IO;
using System.Threading.Tasks;
using Cherry.Media;

namespace Cherry.Flv
{
    /// <summary>
    /// FLV标签类型
    /// </summary>
    public enum FlvTagType : byte
    {
        Audio = 8,
        Video = 9,
        Script = 18
    }

    /// <summary>
    /// FLV文件头
    /// </summary>
    public class FlvHeader
    {
        public const uint Signature = 0x464C5601; // "FLV" + version 1
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }

        public byte[] ToBytes()
        {
            byte flags = 0;
            if (HasVideo) flags |= 0x01;
            if (HasAudio) flags |= 0x04;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(Signature);
            writer.Write(flags);
            writer.Write((uint)9); // Header size
            writer.Write((uint)0); // Previous tag size (0 for header)
            return ms.ToArray();
        }
    }

    /// <summary>
    /// FLV标签
    /// </summary>
    public class FlvTag
    {
        public FlvTagType Type { get; set; }
        public uint DataSize { get; set; }
        public uint Timestamp { get; set; }
        public uint StreamId { get; set; } = 0;
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write((byte)Type);
            writer.Write((byte)(DataSize >> 16));
            writer.Write((byte)(DataSize >> 8));
            writer.Write((byte)DataSize);
            writer.Write((byte)(Timestamp >> 16));
            writer.Write((byte)(Timestamp >> 8));
            writer.Write((byte)Timestamp);
            writer.Write((byte)(StreamId >> 16));
            writer.Write((byte)(StreamId >> 8));
            writer.Write((byte)StreamId);
            writer.Write(Data);

            // Previous tag size
            uint totalSize = 11 + DataSize;
            writer.Write(totalSize);

            return ms.ToArray();
        }
    }

    /// <summary>
    /// FLV输出实现
    /// </summary>
    public class FlvOutput : IMediaOutput
    {
        private Stream? _outputStream;
        private bool _headerWritten;
        private readonly object _lock = new();

        public async Task WriteFrameAsync(MediaFrame frame)
        {
            if (_outputStream == null) return;

            lock (_lock)
            {
                var flvTag = ConvertToFlvTag(frame);
                var bytes = flvTag.ToBytes();
                _outputStream.Write(bytes, 0, bytes.Length);
                _outputStream.Flush();
            }

            await Task.CompletedTask;
        }

        public async Task WriteStreamInfoAsync(MediaStream stream)
        {
            if (_outputStream == null) return;

            lock (_lock)
            {
                if (!_headerWritten)
                {
                    var header = new FlvHeader
                    {
                        HasVideo = stream.VideoCodec != CodecType.Unknown,
                        HasAudio = stream.AudioCodec != CodecType.Unknown
                    };
                    var headerBytes = header.ToBytes();
                    _outputStream.Write(headerBytes, 0, headerBytes.Length);
                    _headerWritten = true;
                }
            }

            await Task.CompletedTask;
        }

        public async Task FlushAsync()
        {
            if (_outputStream != null)
            {
                await _outputStream.FlushAsync();
            }
        }

        public async Task CloseAsync()
        {
            if (_outputStream != null)
            {
                await _outputStream.FlushAsync();
                _outputStream.Close();
                _outputStream = null;
            }
        }

        public void SetOutputStream(Stream stream)
        {
            _outputStream = stream;
            _headerWritten = false;
        }

        private FlvTag ConvertToFlvTag(MediaFrame frame)
        {
            var tag = new FlvTag
            {
                Timestamp = (uint)frame.Timestamp,
                DataSize = (uint)frame.Data.Length
            };

            switch (frame.Type)
            {
                case MediaFrameType.Video:
                    tag.Type = FlvTagType.Video;
                    tag.Data = CreateVideoData(frame);
                    break;
                case MediaFrameType.Audio:
                    tag.Type = FlvTagType.Audio;
                    tag.Data = CreateAudioData(frame);
                    break;
                case MediaFrameType.Metadata:
                    tag.Type = FlvTagType.Script;
                    tag.Data = CreateScriptData(frame);
                    break;
            }

            return tag;
        }

        private byte[] CreateVideoData(MediaFrame frame)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // FLV video tag format
            byte frameType = frame.IsKeyFrame ? (byte)0x10 : (byte)0x20; // Key frame or inter frame
            byte codecId = GetCodecId(frame.Codec);
            byte videoTag = (byte)(frameType | codecId);

            writer.Write(videoTag);
            writer.Write((byte)0); // AVCPacketType (AVC sequence header or NALU)
            writer.Write((byte)0); // CompositionTime
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(frame.Data);

            return ms.ToArray();
        }

        private byte[] CreateAudioData(MediaFrame frame)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // FLV audio tag format
            byte soundFormat = GetSoundFormat(frame.Codec);
            byte soundRate = 0x02; // 44kHz
            byte soundSize = 0x01; // 16-bit
            byte soundType = 0x01; // Stereo
            byte audioTag = (byte)((soundFormat << 4) | (soundRate << 2) | (soundSize << 1) | soundType);

            writer.Write(audioTag);
            writer.Write(frame.Data);

            return ms.ToArray();
        }

        private byte[] CreateScriptData(MediaFrame frame)
        {
            // Script data for metadata
            return frame.Data;
        }

        private byte GetCodecId(CodecType codec)
        {
            return codec switch
            {
                CodecType.H264 => 7,
                CodecType.H265 => 12,
                _ => 0
            };
        }

        private byte GetSoundFormat(CodecType codec)
        {
            return codec switch
            {
                CodecType.AAC => 10,
                CodecType.MP3 => 2,
                _ => 0
            };
        }
    }
}

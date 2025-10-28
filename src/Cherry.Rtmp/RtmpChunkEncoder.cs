using System;
using System.Collections.Generic;
using System.IO;

namespace Cherry.Rtmp
{
    /// <summary>
    /// 简单的 RTMP chunk 封包器：将一个完整消息分割为多个 chunk bytes
    /// 支持 fmt 0（完整头）和 fmt 3（仅 basic header）用于后续 chunk
    /// </summary>
    public class RtmpChunkEncoder
    {
        public int ChunkSize { get; set; } = RtmpConstants.DefaultChunkSize;

        /// <summary>
        /// 当编码出每个 chunk 字节数组时触发（可用于记录或发送前检查）
        /// </summary>
        public event Action<byte[]>? ChunkEncoded;

        /// <summary>
        /// 将一条完整消息编码为一系列 chunk 字节
        /// </summary>
        public IEnumerable<byte[]> Encode(RtmpMessage message)
        {
            var header = message.Header;
            var payload = message.Payload ?? Array.Empty<byte>();

            int offset = 0;
            bool firstChunk = true;

            while (offset < payload.Length)
            {
                int thisSize = Math.Min(ChunkSize, payload.Length - offset);
                using var ms = new MemoryStream();

                // basic header
                byte basic;
                if (header.ChunkStreamId <= 63)
                {
                    basic = (byte)((firstChunk ? 0u : (3u << 6)) | (header.ChunkStreamId & 0x3F));
                    // 当 fmt=0（完整头）时，fmt bits = 0; 对后续 chunk 使用 fmt=3
                    ms.WriteByte(basic);
                }
                else if (header.ChunkStreamId < 320)
                {
                    // 2字节 csid
                    basic = (byte)((firstChunk ? 0u : (3u << 6)) | 0);
                    ms.WriteByte(basic);
                    ms.WriteByte((byte)(header.ChunkStreamId - 64));
                }
                else
                {
                    // 简化实现，使用3字节 csid（rare）
                    basic = (byte)((firstChunk ? 0u : (3u << 6)) | 1);
                    ms.WriteByte(basic);
                    ms.WriteByte((byte)((header.ChunkStreamId - 64) & 0xFF));
                    ms.WriteByte((byte)(((header.ChunkStreamId - 64) >> 8) & 0xFF));
                }

                if (firstChunk)
                {
                    // fmt=0 的完整头：timestamp(3) + msgLen(3) + msgType(1) + streamId(4 little-endian)
                    WriteUInt24(ms, header.Timestamp);
                    WriteUInt24(ms, header.MessageLength);
                    ms.WriteByte((byte)header.MessageType);
                    WriteUInt32LittleEndian(ms, header.MessageStreamId);
                    if (header.Timestamp >= RtmpConstants.ExtendedTimestamp)
                    {
                        // 扩展时间戳
                        WriteUInt32BigEndian(ms, header.Timestamp);
                    }
                }

                // payload
                ms.Write(payload, offset, thisSize);

                offset += thisSize;
                firstChunk = false;

                var chunk = ms.ToArray();
                try { ChunkEncoded?.Invoke(chunk); } catch { }
                yield return chunk;
            }

            // 处理空 payload 的消息（零长度消息也应产出一个 chunk）
            if (payload.Length == 0)
            {
                using var ms2 = new MemoryStream();
                byte basic = (byte)((0u) | (header.ChunkStreamId <= 63 ? (header.ChunkStreamId & 0x3F) : 0));
                ms2.WriteByte(basic);
                WriteUInt24(ms2, header.Timestamp);
                WriteUInt24(ms2, header.MessageLength);
                ms2.WriteByte((byte)header.MessageType);
                WriteUInt32LittleEndian(ms2, header.MessageStreamId);
                yield return ms2.ToArray();
            }
        }

        private static void WriteUInt24(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
        }

        private static void WriteUInt32LittleEndian(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }

        private static void WriteUInt32BigEndian(Stream s, uint v)
        {
            s.WriteByte((byte)((v >> 24) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)(v & 0xFF));
        }
    }
}

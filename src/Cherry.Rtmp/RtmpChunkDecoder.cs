using System;
using System.Collections.Generic;
using System.IO;

namespace Cherry.Rtmp
{
    /// <summary>
    /// 已装配完成的RTMP消息（由解包器输出）
    /// </summary>
    public class RtmpMessage
    {
        public RtmpChunkHeader Header { get; set; } = null!;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// RTMP chunk 解包器：接受字节流，按 chunkSize 拆解并重组完整消息
    /// 支持基本头格式 0~3，扩展时间戳，以及按 chunk 流状态拼接消息。
    /// </summary>
    public class RtmpChunkDecoder
    {
        private int _chunkSize = RtmpConstants.DefaultChunkSize;
        /// <summary>
        /// 最大可接受的 RTMP 消息长度（字节）。超过此大小将被视为协议/数据错误并触发异常。
        /// 默认 10 MB，可根据需要调整或从配置注入。
        /// </summary>
        public int MaxMessageSize { get; set; } = 10 * 1024 * 1024; // 10 MB

        // 每个 chunk stream 的状态
        private readonly Dictionary<uint, ChunkState> _states = new();

        private class ChunkState
        {
            public RtmpChunkHeader? Header;
            public MemoryStream Buffer = new();
            public uint BytesNeeded => Header == null ? 0u : (Header.MessageLength > (uint)Buffer.Length ? Header.MessageLength - (uint)Buffer.Length : 0u);
        }

        public int ChunkSize
        {
            get => _chunkSize;
            set => _chunkSize = Math.Max(1, value);
        }

        /// <summary>
        /// 将一段数据送入解包器，返回0或多个已完成的消息
        /// </summary>
        /// <summary>
        /// 当解出完整消息时触发（实时回调）
        /// </summary>
        public event Action<RtmpMessage>? MessageDecoded;

        public IEnumerable<RtmpMessage> Feed(ReadOnlySpan<byte> input)
        {
            var results = new List<RtmpMessage>();
            int offset = 0;

            while (offset < input.Length)
            {
                // 解析 basic header
                if (offset >= input.Length) break;
                byte first = input[offset++];
                byte fmt = (byte)(first >> 6);
                uint csid = (uint)(first & 0x3F);

                if (csid == 0)
                {
                    if (offset >= input.Length) break; // 等待更多数据
                    csid = (uint)(64 + input[offset++]);
                }
                else if (csid == 1)
                {
                    if (offset + 1 >= input.Length) break;
                    csid = (uint)(64 + input[offset] + (input[offset + 1] << 8));
                    offset += 2;
                }

                // 确保有状态对象
                if (!_states.TryGetValue(csid, out var state))
                {
                    state = new ChunkState();
                    _states[csid] = state;
                }

                // 解析消息头（根据fmt 长度不同）
                int neededHeaderBytes = fmt switch
                {
                    0 => 11,
                    1 => 7,
                    2 => 3,
                    3 => 0,
                    _ => 0
                };

                if (offset + neededHeaderBytes > input.Length)
                {
                    // 不够头部字节，回退 offset 到 basic header 开始的下一个循环（这里简单中断等待更多数据）
                    break;
                }

                var header = new RtmpChunkHeader();
                header.Format = fmt;
                header.ChunkStreamId = csid;

                if (fmt == 0)
                {
                    header.Timestamp = ReadUInt24(input, offset);
                    header.MessageLength = ReadUInt24(input, offset + 3);
                    header.MessageType = (RtmpMessageType)input[offset + 6];
                    header.MessageStreamId = ReadUInt32LittleEndian(input, offset + 7);
                    offset += 11;

                    // 扩展时间戳
                    if (header.Timestamp == RtmpConstants.ExtendedTimestamp)
                    {
                        if (offset + 4 > input.Length) break;
                        header.Timestamp = ReadUInt32BigEndian(input, offset);
                        offset += 4;
                    }
                }
                else if (fmt == 1)
                {
                    header.Timestamp = ReadUInt24(input, offset);
                    header.MessageLength = ReadUInt24(input, offset + 3);
                    header.MessageType = (RtmpMessageType)input[offset + 6];
                    offset += 7;
                    if (header.Timestamp == RtmpConstants.ExtendedTimestamp)
                    {
                        if (offset + 4 > input.Length) break;
                        header.Timestamp = ReadUInt32BigEndian(input, offset);
                        offset += 4;
                    }
                }
                else if (fmt == 2)
                {
                    header.Timestamp = ReadUInt24(input, offset);
                    offset += 3;
                    if (header.Timestamp == RtmpConstants.ExtendedTimestamp)
                    {
                        if (offset + 4 > input.Length) break;
                        header.Timestamp = ReadUInt32BigEndian(input, offset);
                        offset += 4;
                    }
                    // 消息长度/类型/streamid 复用上一次的
                    if (state.Header != null)
                    {
                        header.MessageLength = state.Header.MessageLength;
                        header.MessageType = state.Header.MessageType;
                        header.MessageStreamId = state.Header.MessageStreamId;
                    }
                }
                else // fmt == 3
                {
                    // 完全复用上一次头
                    if (state.Header == null)
                    {
                        // 没有上一次头，协议错误—跳出
                        break;
                    }
                    header = state.Header;
                }

                // 更新状态的头（当fmt!=3时会替换）
                if (fmt != 3)
                {
                    state.Header = header;
                    // 新的消息开始时，若缓冲区已有数据但之前未完成，保留（这是chunk流继续拼接）
                }

                // Sanity check: reject obviously too-large messages to avoid memory exhaustion or protocol abuse
                if (state.Header != null && state.Header.MessageLength > (uint)MaxMessageSize)
                {
                    // 抛出异常，上层连接应捕获并关闭连接
                    throw new InvalidDataException($"RTMP message length {state.Header.MessageLength} exceeds configured MaxMessageSize={MaxMessageSize}");
                }

                // 读取 chunk 数据（当前 chunk 的 payload 部分）
                uint remaining = header.MessageLength > (uint)state.Buffer.Length ? header.MessageLength - (uint)state.Buffer.Length : 0u;
                int toRead = (int)Math.Min(remaining, (uint)_chunkSize);
                if (offset + toRead > input.Length)
                {
                    // 数据不完整，读取能读到的部分并等待下一次
                    toRead = input.Length - offset;
                }

                if (toRead > 0)
                {
                    state.Buffer.Write(input.Slice(offset, toRead));
                    offset += toRead;
                }

                // 如果已组装完成一条消息，产出
                if (state.Header != null && state.Buffer.Length >= state.Header.MessageLength)
                {
                    var msg = new RtmpMessage
                    {
                        Header = state.Header,
                        Payload = state.Buffer.ToArray()
                    };
                    results.Add(msg);
                    // 实时触发事件
                    try { MessageDecoded?.Invoke(msg); } catch { }

                    // 清空缓冲区，保留 header 为 null（下一次读取新的消息，header 会在后续 basic header 更新）
                    state.Buffer = new MemoryStream();
                    state.Header = null;
                }
            }

            return results;
        }

        private static uint ReadUInt24(ReadOnlySpan<byte> buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16));
        }

        private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buf, int offset)
        {
            return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
        }
    }
}

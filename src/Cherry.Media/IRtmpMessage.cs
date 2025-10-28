using System;

namespace Cherry.Media
{
    /// <summary>
    /// Media 层与 RTMP 层之间的轻量桥接：表示一个 RTMP 消息的数据契约
    /// 由 Cherry.Rtmp 提供具体实现，Media 层可依赖该接口进行抽象处理
    /// </summary>
    public interface IRtmpMessage
    {
        /// <summary>
        /// 消息类型 id
        /// </summary>
        byte MessageTypeId { get; }

        /// <summary>
        /// 消息流 id
        /// </summary>
        uint MessageStreamId { get; }

        /// <summary>
        /// 时间戳（ms）
        /// </summary>
        uint Timestamp { get; }

        /// <summary>
        /// 原始有效负载
        /// </summary>
        ReadOnlyMemory<byte> Payload { get; }
    }
}

using System;

namespace Cherry.Rtmp
{
    internal static class RtmpConstants
    {
        // 默认chunk size (可通过SetChunkSize消息调整)
        public const int DefaultChunkSize = 128;
        public const uint ExtendedTimestamp = 0xFFFFFF;
    }
}

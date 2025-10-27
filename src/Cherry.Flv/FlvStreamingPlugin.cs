using System;
using System.IO;
using System.Threading.Tasks;
using Cherry.Media;

namespace Cherry.Flv
{
    /// <summary>
    /// FLV流媒体插件接口
    /// </summary>
    public interface IFlvStreamingPlugin
    {
        /// <summary>
        /// 插件名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 初始化插件
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 启动流
        /// </summary>
        Task StartStreamAsync(string streamKey, MediaStream streamInfo);

        /// <summary>
        /// 停止流
        /// </summary>
        Task StopStreamAsync(string streamKey);

        /// <summary>
        /// 写入媒体帧
        /// </summary>
        Task WriteFrameAsync(string streamKey, MediaFrame frame);

        /// <summary>
        /// 获取FLV流
        /// </summary>
        Task<Stream?> GetFlvStreamAsync(string streamKey);

        /// <summary>
        /// 获取流信息
        /// </summary>
        Task<FlvStreamInfo?> GetStreamInfoAsync(string streamKey);

        /// <summary>
        /// 获取所有活动流
        /// </summary>
        Task<string[]> GetActiveStreamsAsync();

        /// <summary>
        /// 清理资源
        /// </summary>
        Task CleanupAsync();
    }

    /// <summary>
    /// FLV流信息
    /// </summary>
    public class FlvStreamInfo
    {
        public string StreamKey { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime StartTime { get; set; }
        public int FrameCount { get; set; }
        public TimeSpan Duration { get; set; }
        public CodecType VideoCodec { get; set; }
        public CodecType AudioCodec { get; set; }
        public int Bitrate { get; set; }
    }

    /// <summary>
    /// 默认FLV流媒体插件实现
    /// </summary>
    public class DefaultFlvStreamingPlugin : IFlvStreamingPlugin
    {
        public string Name => "Default FLV Streaming";

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FlvStreamSession> _activeStreams = new();

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task StartStreamAsync(string streamKey, MediaStream streamInfo)
        {
            var session = new FlvStreamSession(streamKey, streamInfo);
            await session.InitializeAsync();
            _activeStreams[streamKey] = session;
        }

        public async Task StopStreamAsync(string streamKey)
        {
            if (_activeStreams.TryRemove(streamKey, out var session))
            {
                await session.CloseAsync();
            }
        }

        public async Task WriteFrameAsync(string streamKey, MediaFrame frame)
        {
            if (_activeStreams.TryGetValue(streamKey, out var session))
            {
                await session.WriteFrameAsync(frame);
            }
        }

        public async Task<Stream?> GetFlvStreamAsync(string streamKey)
        {
            if (_activeStreams.TryGetValue(streamKey, out var session))
            {
                return await session.GetStreamAsync();
            }
            return null;
        }

        public async Task<FlvStreamInfo?> GetStreamInfoAsync(string streamKey)
        {
            if (_activeStreams.TryGetValue(streamKey, out var session))
            {
                return await session.GetInfoAsync();
            }
            return null;
        }

        public async Task<string[]> GetActiveStreamsAsync()
        {
            return _activeStreams.Keys.ToArray();
        }

        public async Task CleanupAsync()
        {
            foreach (var session in _activeStreams.Values)
            {
                await session.CloseAsync();
            }
            _activeStreams.Clear();
        }
    }

    /// <summary>
    /// FLV流会话
    /// </summary>
    internal class FlvStreamSession
    {
        private readonly string _streamKey;
        private readonly MediaStream _streamInfo;
        private readonly FlvOutput _flvOutput;
        private readonly MemoryStream _buffer = new();
        private bool _isInitialized;
        private DateTime _startTime;
        private int _frameCount;

        public FlvStreamSession(string streamKey, MediaStream streamInfo)
        {
            _streamKey = streamKey;
            _streamInfo = streamInfo;
            _flvOutput = new FlvOutput();
        }

        public async Task InitializeAsync()
        {
            _flvOutput.SetOutputStream(_buffer);
            await _flvOutput.WriteStreamInfoAsync(_streamInfo);
            _isInitialized = true;
            _startTime = DateTime.UtcNow;
            _frameCount = 0;
        }

        public async Task WriteFrameAsync(MediaFrame frame)
        {
            if (!_isInitialized) return;

            await _flvOutput.WriteFrameAsync(frame);
            _frameCount++;
        }

        public async Task<Stream> GetStreamAsync()
        {
            // 返回缓冲区的副本以支持并发访问
            var copy = new MemoryStream();
            _buffer.Position = 0;
            await _buffer.CopyToAsync(copy);
            copy.Position = 0;
            return copy;
        }

        public async Task<FlvStreamInfo> GetInfoAsync()
        {
            return new FlvStreamInfo
            {
                StreamKey = _streamKey,
                IsActive = _isInitialized,
                StartTime = _startTime,
                FrameCount = _frameCount,
                Duration = _isInitialized ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                VideoCodec = _streamInfo.VideoCodec,
                AudioCodec = _streamInfo.AudioCodec,
                Bitrate = 0 // TODO: 计算比特率
            };
        }

        public async Task CloseAsync()
        {
            await _flvOutput.CloseAsync();
            _buffer.Dispose();
        }
    }
}
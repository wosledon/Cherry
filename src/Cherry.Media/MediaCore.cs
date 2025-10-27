using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cherry.Media
{
    /// <summary>
    /// 媒体帧类型
    /// </summary>
    public enum MediaFrameType
    {
        Video,
        Audio,
        Metadata
    }

    /// <summary>
    /// 编解码器类型
    /// </summary>
    public enum CodecType
    {
        H264,
        H265,
        AAC,
        MP3,
        Unknown
    }

    /// <summary>
    /// 媒体帧
    /// </summary>
    public class MediaFrame
    {
        public MediaFrameType Type { get; set; }
        public CodecType Codec { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; }
        public bool IsKeyFrame { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();
    }

    /// <summary>
    /// 媒体流信息
    /// </summary>
    public class MediaStream
    {
        public string StreamId { get; set; } = string.Empty;
        public CodecType VideoCodec { get; set; }
        public CodecType AudioCodec { get; set; }
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public int AudioChannels { get; set; }
        public int AudioSampleRate { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();
    }

    /// <summary>
    /// 媒体输入接口
    /// </summary>
    public interface IMediaInput
    {
        event EventHandler<MediaFrame>? FrameReceived;
        event EventHandler<MediaStream>? StreamInfoReceived;

        Task StartAsync(string url);
        Task StopAsync();
        bool IsRunning { get; }
    }

    /// <summary>
    /// 媒体输出接口
    /// </summary>
    public interface IMediaOutput
    {
        Task WriteFrameAsync(MediaFrame frame);
        Task WriteStreamInfoAsync(MediaStream stream);
        Task FlushAsync();
        Task CloseAsync();
    }

    /// <summary>
    /// 编解码器接口
    /// </summary>
    public interface ICodec
    {
        CodecType InputType { get; }
        CodecType OutputType { get; }
        Task<MediaFrame> EncodeAsync(MediaFrame frame);
        Task<MediaFrame> DecodeAsync(MediaFrame frame);
    }

    /// <summary>
    /// 媒体处理器
    /// </summary>
    public class MediaProcessor
    {
        private readonly IMediaInput _input;
        private readonly IMediaOutput _output;
        private readonly ICodec? _codec;
        private readonly CancellationTokenSource _cts = new();

        public MediaProcessor(IMediaInput input, IMediaOutput output, ICodec? codec = null)
        {
            _input = input;
            _output = output;
            _codec = codec;

            _input.FrameReceived += OnFrameReceived;
            _input.StreamInfoReceived += OnStreamInfoReceived;
        }

        public async Task StartAsync(string inputUrl)
        {
            await _output.WriteStreamInfoAsync(new MediaStream()); // 初始化
            await _input.StartAsync(inputUrl);

            // 等待停止信号
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            await _input.StopAsync();
            await _output.CloseAsync();
        }

        private async void OnFrameReceived(object? sender, MediaFrame frame)
        {
            try
            {
                MediaFrame processedFrame = frame;

                // 如果有编解码器，处理帧
                if (_codec != null)
                {
                    processedFrame = await _codec.EncodeAsync(frame);
                }

                await _output.WriteFrameAsync(processedFrame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing frame: {ex.Message}");
            }
        }

        private async void OnStreamInfoReceived(object? sender, MediaStream stream)
        {
            try
            {
                await _output.WriteStreamInfoAsync(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing stream info: {ex.Message}");
            }
        }
    }
}

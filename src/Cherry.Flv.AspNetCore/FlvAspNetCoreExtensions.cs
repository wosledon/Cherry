using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cherry.Media;
using Cherry.Rtmp.Server;

namespace Cherry.Flv.AspNetCore
{
    /// <summary>
    /// FLV流媒体ASP.NET Core扩展
    /// </summary>
    public static class FlvStreamingExtensions
    {
        /// <summary>
        /// 添加FLV流媒体服务
        /// </summary>
        public static IServiceCollection AddFlvStreaming(this IServiceCollection services, Action<FlvStreamingOptions>? configure = null)
        {
            var options = new FlvStreamingOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton<IFlvStreamingPlugin>(options.PluginFactory());
            services.AddSingleton<FlvStreamingService>();
            services.AddHostedService<FlvStreamingBackgroundService>();

            return services;
        }

        /// <summary>
        /// 使用FLV流媒体中间件
        /// </summary>
        public static IApplicationBuilder UseFlvStreaming(this IApplicationBuilder app)
        {
            var streamingService = app.ApplicationServices.GetRequiredService<FlvStreamingService>();
            var options = app.ApplicationServices.GetRequiredService<FlvStreamingOptions>();

            // 注册RTMP服务器事件
            if (options.RtmpServer != null)
            {
                options.RtmpServer.StreamStarted += async (sender, streamKey) =>
                {
                    var streamInfo = options.RtmpServer.GetStream(streamKey);
                    if (streamInfo != null)
                    {
                        await streamingService.StartStreamAsync(streamKey, streamInfo.StreamInfo);
                    }
                };

                options.RtmpServer.StreamStopped += async (sender, streamKey) =>
                {
                    await streamingService.StopStreamAsync(streamKey);
                };

                options.RtmpServer.StreamFrameReceived += async (sender, frame) =>
                {
                    // 将帧分发到所有相关的FLV流
                    var activeStreams = await streamingService.GetActiveStreamsAsync();
                    foreach (var streamKey in activeStreams)
                    {
                        await streamingService.WriteFrameAsync(streamKey, frame);
                    }
                };
            }

            return app;
        }

        /// <summary>
        /// 映射FLV流媒体端点
        /// </summary>
        public static IEndpointRouteBuilder MapFlvStreaming(this IEndpointRouteBuilder endpoints, string pattern = "/flv/{streamKey}")
        {
            var streamingService = endpoints.ServiceProvider.GetRequiredService<FlvStreamingService>();

            endpoints.MapGet(pattern, async (string streamKey) =>
            {
                var stream = await streamingService.GetFlvStreamAsync(streamKey);
                if (stream == null)
                {
                    return Results.NotFound($"Stream {streamKey} not found");
                }

                return Results.Stream(stream, "video/x-flv", enableRangeProcessing: false);
            });

            endpoints.MapGet("/api/flv/streams", async () =>
            {
                var streams = await streamingService.GetActiveStreamsAsync();
                return Results.Ok(streams);
            });

            endpoints.MapGet("/api/flv/streams/{streamKey}/info", async (string streamKey) =>
            {
                var info = await streamingService.GetStreamInfoAsync(streamKey);
                if (info == null)
                {
                    return Results.NotFound($"Stream {streamKey} not found");
                }

                return Results.Ok(info);
            });

            return endpoints;
        }
    }

    /// <summary>
    /// FLV流媒体选项
    /// </summary>
    public class FlvStreamingOptions
    {
        /// <summary>
        /// RTMP服务器实例
        /// </summary>
        public RtmpServer? RtmpServer { get; set; }

        /// <summary>
        /// 配置管理器
        /// </summary>
        public DefaultServerConfigManager? ConfigManager { get; set; }

        /// <summary>
        /// 插件工厂方法
        /// </summary>
        public Func<IFlvStreamingPlugin> PluginFactory { get; set; } = () => new DefaultFlvStreamingPlugin();
    }

    /// <summary>
    /// FLV流媒体服务
    /// </summary>
    public class FlvStreamingService
    {
        private readonly IFlvStreamingPlugin _plugin;

        public FlvStreamingService(IFlvStreamingPlugin plugin)
        {
            _plugin = plugin;
        }

        public Task InitializeAsync() => _plugin.InitializeAsync();
        public Task StartStreamAsync(string streamKey, MediaStream streamInfo) => _plugin.StartStreamAsync(streamKey, streamInfo);
        public Task StopStreamAsync(string streamKey) => _plugin.StopStreamAsync(streamKey);
        public Task WriteFrameAsync(string streamKey, MediaFrame frame) => _plugin.WriteFrameAsync(streamKey, frame);
        public Task<Stream?> GetFlvStreamAsync(string streamKey) => _plugin.GetFlvStreamAsync(streamKey);
        public Task<FlvStreamInfo?> GetStreamInfoAsync(string streamKey) => _plugin.GetStreamInfoAsync(streamKey);
        public Task<string[]> GetActiveStreamsAsync() => _plugin.GetActiveStreamsAsync();
        public Task CleanupAsync() => _plugin.CleanupAsync();
    }

    /// <summary>
    /// FLV流媒体后台服务
    /// </summary>
    public class FlvStreamingBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
    {
        private readonly FlvStreamingService _streamingService;
        private readonly FlvStreamingOptions _options;

        public FlvStreamingBackgroundService(FlvStreamingService streamingService, FlvStreamingOptions options)
        {
            _streamingService = streamingService;
            _options = options;
        }

        protected override async Task ExecuteAsync(System.Threading.CancellationToken stoppingToken)
        {
            // 保存配置
            if (_options.ConfigManager != null)
            {
                await _options.ConfigManager.SaveConfigAsync();
            }

            await _streamingService.InitializeAsync();

            // 启动RTMP服务器
            if (_options.RtmpServer != null)
            {
                await _options.RtmpServer.StartAsync();
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            // 停止RTMP服务器
            if (_options.RtmpServer != null)
            {
                _options.RtmpServer.Stop();
            }

            await _streamingService.CleanupAsync();
        }
    }
}
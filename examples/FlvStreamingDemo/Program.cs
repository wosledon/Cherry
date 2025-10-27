using Cherry.Media;
using Cherry.Rtmp.Server;
using Cherry.Flv.AspNetCore;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();

// 添加CORS服务
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// 配置FLV流媒体服务
builder.Services.AddFlvStreaming(options =>
{
    // 创建RTMP服务器
    var configManager = new DefaultServerConfigManager("server_config.json");

    // 添加默认虚拟主机
    var vhost = new VirtualHostConfig
    {
        Name = "default",
        RequireAuth = false
    };

    var stream = new StreamConfig
    {
        Key = "live",
        IsPublishing = true,
        IsPlaying = true,
        MaxConnections = 100
    };

    // 先添加虚拟主机，再添加流
    configManager.AddVirtualHost("default", vhost);
    configManager.AddStream("default", "live", stream);

    var rtmpServer = new RtmpServer(configManager);
    options.RtmpServer = rtmpServer;
    options.ConfigManager = configManager; // 添加配置管理器引用
});

// builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Swagger UI removed for simplicity
}

app.UseHttpsRedirection();

// 启用路由
app.UseRouting();

// 启用CORS
app.UseCors("AllowAll");

// 提供静态文件
app.UseDefaultFiles();
app.UseStaticFiles();

// 使用FLV流媒体中间件
app.UseFlvStreaming();

// 映射FLV流媒体端点
app.MapFlvStreaming();

// 额外的API端点
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/api/rtmp/start", async (IServiceProvider services) =>
{
    var options = services.GetRequiredService<FlvStreamingOptions>();
    if (options.RtmpServer != null)
    {
        _ = Task.Run(async () => await options.RtmpServer.StartAsync());
        return Results.Ok(new { message = "RTMP server started", port = 1935 });
    }
    return Results.BadRequest("RTMP server not configured");
});

app.MapPost("/api/rtmp/stop", (IServiceProvider services) =>
{
    var options = services.GetRequiredService<FlvStreamingOptions>();
    if (options.RtmpServer != null)
    {
        options.RtmpServer.Stop();
        return Results.Ok(new { message = "RTMP server stopped" });
    }
    return Results.BadRequest("RTMP server not configured");
});

app.Run();
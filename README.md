# Cherry Media Framework

Cherry是一个通用的媒体处理框架，支持多种输入协议和输出格式的灵活组合。

## 🚀 MVP版本 - FLV流媒体直播

**立即可用功能**：
- ✅ OBS推流到RTMP服务器 (`rtmp://localhost:1935/live`)
- ✅ ASP.NET Core Web界面播放FLV直播流
- ✅ 实时流管理和监控API
- ✅ 插件化架构，支持扩展

### 快速开始

```bash
# 运行演示应用
cd examples/FlvStreamingDemo
dotnet run

# 访问 http://localhost:5000
# 使用OBS推流到 rtmp://localhost:1935/live
```

### OBS设置
- **流类型**: 自定义流媒体服务器
- **URL**: `rtmp://localhost:1935/live`
- **流密钥**: `live`

---

## 架构概述

```
输入协议 (RTMP, RTSP, RTP) → 媒体处理器 → 输出格式 (FLV, HLS, MP4)
                                      ↓
                                 编解码器 (可选)
```

## 核心组件

### 1. 媒体数据模型

#### MediaFrame
表示单个媒体帧，包含：
- `Type`: 帧类型 (Video/Audio/Metadata)
- `Codec`: 编解码器类型 (H264/H265/AAC/MP3)
- `Data`: 帧数据
- `Timestamp`: 时间戳
- `IsKeyFrame`: 是否为关键帧
- `Metadata`: 附加元数据

#### MediaStream
表示媒体流信息，包含：
- `StreamId`: 流标识符
- `VideoCodec/AudioCodec`: 视频/音频编解码器
- `VideoWidth/VideoHeight`: 视频分辨率
- `AudioChannels/AudioSampleRate`: 音频参数

### 2. 接口定义

#### IMediaInput
媒体输入接口，支持事件驱动的数据接收：
- `FrameReceived`: 收到媒体帧时触发
- `StreamInfoReceived`: 收到流信息时触发
- `StartAsync(url)`: 开始接收媒体流
- `StopAsync()`: 停止接收

#### IMediaOutput
媒体输出接口：
- `WriteFrameAsync(frame)`: 写入媒体帧
- `WriteStreamInfoAsync(stream)`: 写入流信息
- `FlushAsync()`: 刷新缓冲区
- `CloseAsync()`: 关闭输出

#### ICodec
编解码器接口：
- `InputType/OutputType`: 输入/输出编解码器类型
- `EncodeAsync(frame)`: 编码帧
- `DecodeAsync(frame)`: 解码帧

### 3. 媒体处理器 (MediaProcessor)

核心协调器，将输入、输出和编解码器组合在一起：
- 自动处理帧接收和转发
- 支持可选的编解码器处理
- 异步事件驱动架构

## 支持的协议和格式

### 输入协议

#### RTMP (Real Time Messaging Protocol)
- 实现: `RtmpInput`
- 支持: RTMP流接收，自动解析FLV标签
- 状态: 已实现

#### RTSP (Real Time Streaming Protocol)
- 实现: `RtspInput`
- 支持: RTSP over TCP，RTP数据解析
- 状态: 已实现

#### RTP (Real-time Transport Protocol)
- 实现: 计划中
- 支持: 纯RTP流接收
- 状态: 待实现

### 输出格式

#### FLV (Flash Video)
- 实现: `FlvOutput`
- 支持: FLV文件写入，自动生成FLV头和标签
- 状态: 已实现

#### HLS (HTTP Live Streaming)
- 实现: 计划中
- 支持: M3U8播放列表和TS分片生成
- 状态: 待实现

#### MP4
- 实现: 计划中
- 支持: MP4容器格式
- 状态: 待实现

## 使用示例

### RTMP转FLV

```csharp
using Cherry.Media;
using Cherry.Rtmp;
using Cherry.Flv;

// 创建输入
var input = new RtmpInput();

// 创建输出
var output = new FlvOutput();
var fileStream = File.Create("output.flv");
output.SetOutputStream(fileStream);

// 创建处理器
var processor = new MediaProcessor(input, output);

// 开始处理
await processor.StartAsync("rtmp://example.com/live/stream");

// 运行30秒后停止
await Task.Delay(30000);
await processor.StopAsync();

fileStream.Close();
```

### RTSP转FLV

```csharp
using Cherry.Media;
using Cherry.Rtsp;
using Cherry.Flv;

// 创建输入
var input = new RtspInput();

// 创建输出
var output = new FlvOutput();
var fileStream = File.Create("output.flv");
output.SetOutputStream(fileStream);

// 创建处理器
var processor = new MediaProcessor(input, output);

// 开始处理
await processor.StartAsync("rtsp://example.com/live/stream");

// 运行30秒后停止
await Task.Delay(30000);
await processor.StopAsync();

fileStream.Close();
```

### 带编解码器的处理

```csharp
// 创建自定义编解码器
var codec = new MyCodec();

// 创建处理器（带编解码器）
var processor = new MediaProcessor(input, output, codec);
```

## 扩展开发

### 添加新的输入协议

1. 实现 `IMediaInput` 接口
2. 处理协议特定的连接和数据接收
3. 将接收到的数据转换为 `MediaFrame` 和 `MediaStream` 对象
4. 触发相应的事件

### 添加新的输出格式

1. 实现 `IMediaOutput` 接口
2. 处理格式特定的文件头和数据写入
3. 支持流信息的写入（如编解码器信息、分辨率等）

### 添加新的编解码器

1. 实现 `ICodec` 接口
2. 指定输入和输出编解码器类型
3. 实现编码和解码逻辑

## 项目结构

```
Cherry/
├── src/
│   ├── Cherry.Media/          # 核心框架
│   │   └── MediaCore.cs       # 数据模型和接口定义
│   ├── Cherry.Rtmp/           # RTMP协议支持
│   │   └── Class1.cs          # RtmpInput实现
│   ├── Cherry.Rtsp/           # RTSP协议支持
│   │   ├── RtspMessage.cs     # RTSP消息处理
│   │   ├── RtspParser.cs      # RTSP解析器
│   │   ├── Authentication.cs  # 认证支持
│   │   └── RtspInput.cs       # RtspInput实现
│   ├── Cherry.Flv/            # FLV格式支持
│   │   └── Class1.cs          # FlvOutput实现
│   └── Cherry.Rtmp.*          # RTMP客户端/服务器
├── samples/                   # 示例代码
│   ├── MediaDemo.cs          # 框架使用演示
│   └── AuthTest.cs           # 认证测试
└── docs/                     # 文档
    ├── rtsp_auth_readme.md   # RTSP认证文档
    └── rtsp_readme.md        # RTSP协议文档
```

## 编译和运行

```bash
# 编译整个解决方案
dotnet build Cherry.slnx

# 运行演示程序
cd samples
dotnet run --project MediaDemo.csproj
```

## 特性

- **模块化设计**: 输入、输出、编解码器完全解耦
- **异步架构**: 完全异步的事件驱动处理
- **可扩展性**: 易于添加新的协议和格式
- **类型安全**: 强类型的数据模型
- **高性能**: 零拷贝的数据处理（尽可能）

## 许可证

[待定]
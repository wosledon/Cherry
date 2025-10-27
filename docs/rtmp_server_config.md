# Cherry RTMP Server - Virtual Host Configuration

Cherry RTMP Server 支持虚拟主机和流密钥配置，提供灵活的流媒体服务器管理。

## 功能特性

- **虚拟主机支持**: 支持多个虚拟主机，每个主机可以有独立的配置
- **流密钥管理**: 支持自定义流密钥格式，默认使用 `/live/{Key}` 格式
- **发布授权**: 支持流发布权限验证
- **播放授权**: 支持流播放权限验证
- **配置持久化**: 支持JSON格式的配置文件

## 配置结构

### 服务器配置 (ServerConfiguration)
```json
{
  "rtmpPort": 1935,
  "rtspPort": 554,
  "hlsPort": 8080,
  "virtualHosts": {
    "default": {
      "name": "default",
      "requireAuth": false,
      "users": {},
      "streams": {
        "live": {
          "key": "stream-key-guid",
          "isPublishing": true,
          "isPlaying": true,
          "requireAuth": false,
          "publisherUser": null,
          "publishTokens": [],
          "playTokens": [],
          "maxConnections": 100,
          "maxDuration": null
        }
      }
    }
  }
}
```

### 虚拟主机配置 (VirtualHostConfig)
- `name`: 虚拟主机名称
- `requireAuth`: 是否需要认证
- `users`: 用户名密码映射
- `streams`: 流配置字典

### 流配置 (StreamConfig)
- `key`: 流密钥
- `isPublishing`: 是否允许发布
- `isPlaying`: 是否允许播放
- `requireAuth`: 是否需要认证
- `publisherUser`: 发布者用户名
- `publishTokens`: 发布令牌集合
- `playTokens`: 播放令牌集合
- `maxConnections`: 最大连接数
- `maxDuration`: 最大持续时间

## 使用方法

### 1. 创建配置管理器
```csharp
// 使用默认实现
var configManager = new DefaultServerConfigManager("server_config.json");

// 或者继承自定义实现
public class CustomConfigManager : ServerConfigManager
{
    public override bool ValidatePublishAuth(string vhostName, string streamKey, string? authToken = null)
    {
        // 自定义验证逻辑
        return true;
    }

    public override bool ValidatePlayAuth(string vhostName, string streamKey, string? authToken = null)
    {
        // 自定义验证逻辑
        return true;
    }
}
```

### 2. 添加虚拟主机
```csharp
var vhost = new VirtualHostConfig
{
    Name = "myhost",
    RequireAuth = true
};
configManager.AddVirtualHost("myhost", vhost);
```

### 3. 添加流配置
```csharp
var stream = new StreamConfig
{
    Key = configManager.GenerateStreamKey("live"), // 生成 live/guid 格式的密钥
    IsPublishing = true,
    IsPlaying = true,
    MaxConnections = 50
};
configManager.AddStream("myhost", "live", stream);
```

### 4. 验证权限
```csharp
// 验证发布权限
bool canPublish = configManager.ValidatePublishAuth("myhost", "live/stream-key", authToken);

// 验证播放权限
bool canPlay = configManager.ValidatePlayAuth("myhost", "live/stream-key", authToken);
```

### 5. 保存配置
```csharp
await configManager.SaveConfigAsync();
```

## 流密钥格式

默认流密钥格式为 `/live/{GUID}`，例如：
- `live/12345678-1234-1234-1234-123456789abc`

您可以通过以下方式生成新的流密钥：
```csharp
string streamKey = configManager.GenerateStreamKey("live");
// 输出: live/guid-here
```

## 运行示例

```bash
cd demo
dotnet run
```

这将启动RTMP服务器并监听1935端口，支持虚拟主机配置和流授权。

## 安全注意事项

- 默认情况下，认证是禁用的
- 生产环境中应启用 `requireAuth`
- 使用强密码和令牌
- 定期轮换流密钥
- 监控连接数和持续时间

## 可扩展性

`ServerConfigManager` 提供了可扩展的架构：

- `ValidatePublishAuth` 和 `ValidatePlayAuth` 方法是 `virtual` 的，可以被重写
- 基类提供默认实现抛出 `NotImplementedException`
- `DefaultServerConfigManager` 提供了基于配置文件的标准实现
- 可以继承来自定义认证逻辑（如数据库验证、外部服务等）
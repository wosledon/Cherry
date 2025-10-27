using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cherry.Media
{
    /// <summary>
    /// 流媒体服务器配置管理器
    /// </summary>
    public class ServerConfigManager
    {
        private readonly string _configPath;
        private ServerConfiguration _config;
        private readonly object _lock = new();

        public ServerConfiguration Config => _config;

        public ServerConfigManager(string configPath = "server_config.json")
        {
            _configPath = configPath;
            _config = LoadConfig();
        }

        /// <summary>
        /// 加载配置
        /// </summary>
        private ServerConfiguration LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<ServerConfiguration>(json);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
            }

            // 返回默认配置
            return CreateDefaultConfig();
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        public async Task SaveConfigAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(_configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建默认配置
        /// </summary>
        private ServerConfiguration CreateDefaultConfig()
        {
            var config = new ServerConfiguration
            {
                RtmpPort = 1935,
                RtspPort = 554,
                HlsPort = 8080
            };

            // 默认虚拟主机
            var defaultVhost = new VirtualHostConfig
            {
                Name = "default",
                RequireAuth = false
            };

            // 默认流配置
            var defaultStream = new StreamConfig
            {
                Key = Guid.NewGuid().ToString(),
                IsPublishing = true,
                IsPlaying = true,
                MaxConnections = 100
            };

            defaultVhost.Streams["live"] = defaultStream;
            config.VirtualHosts["default"] = defaultVhost;

            return config;
        }

        /// <summary>
        /// 添加虚拟主机
        /// </summary>
        public void AddVirtualHost(string name, VirtualHostConfig vhost)
        {
            lock (_lock)
            {
                _config.VirtualHosts[name] = vhost;
            }
        }

        /// <summary>
        /// 获取虚拟主机
        /// </summary>
        public VirtualHostConfig? GetVirtualHost(string name)
        {
            lock (_lock)
            {
                return _config.VirtualHosts.GetValueOrDefault(name);
            }
        }

        /// <summary>
        /// 添加流配置
        /// </summary>
        public void AddStream(string vhostName, string appName, StreamConfig stream)
        {
            lock (_lock)
            {
                if (_config.VirtualHosts.TryGetValue(vhostName, out var vhost))
                {
                    vhost.Streams[appName] = stream;
                }
            }
        }

        /// <summary>
        /// 获取流配置
        /// </summary>
        public StreamConfig? GetStream(string vhostName, string appName)
        {
            lock (_lock)
            {
                if (_config.VirtualHosts.TryGetValue(vhostName, out var vhost))
                {
                    return vhost.Streams.GetValueOrDefault(appName);
                }
                return null;
            }
        }

        /// <summary>
        /// 生成新的流key
        /// </summary>
        public string GenerateStreamKey(string app = "live")
        {
            return $"{app}/{Guid.NewGuid().ToString()}";
        }

        /// <summary>
        /// 验证发布权限
        /// </summary>
        public virtual bool ValidatePublishAuth(string vhostName, string streamKey, string? authToken = null)
        {
            throw new NotImplementedException("ValidatePublishAuth must be implemented by derived class");
        }

        /// <summary>
        /// 验证播放权限
        /// </summary>
        public virtual bool ValidatePlayAuth(string vhostName, string streamKey, string? authToken = null)
        {
            throw new NotImplementedException("ValidatePlayAuth must be implemented by derived class");
        }
    }

    /// <summary>
    /// 默认配置管理器实现
    /// </summary>
    public class DefaultServerConfigManager : ServerConfigManager
    {
        public DefaultServerConfigManager(string configPath = "server_config.json") : base(configPath)
        {
        }

        /// <summary>
        /// 验证发布权限 - 默认实现
        /// </summary>
        public override bool ValidatePublishAuth(string vhostName, string streamKey, string? authToken = null)
        {
            var vhost = GetVirtualHost(vhostName);
            if (vhost == null) return false;

            // 解析流key
            var parts = streamKey.Split('/');
            string app;
            string key;

            if (parts.Length >= 2)
            {
                app = parts[0];
                key = string.Join("/", parts.Skip(1));
            }
            else
            {
                app = streamKey;
                key = "";
            }

            var stream = GetStream(vhostName, app);
            if (stream == null) return false;

            // 检查是否需要认证
            if (!vhost.RequireAuth && !stream.RequireAuth)
            {
                return true;
            }

            // 验证认证令牌
            if (!string.IsNullOrEmpty(authToken))
            {
                return stream.PublishTokens.Contains(authToken);
            }

            // 检查用户认证
            return !string.IsNullOrEmpty(stream.PublisherUser);
        }

        /// <summary>
        /// 验证播放权限 - 默认实现
        /// </summary>
        public override bool ValidatePlayAuth(string vhostName, string streamKey, string? authToken = null)
        {
            var stream = GetStream(vhostName, streamKey.Split('/')[0]);
            if (stream == null) return false;

            if (!stream.RequireAuth)
            {
                return true;
            }

            // 验证认证令牌
            if (!string.IsNullOrEmpty(authToken))
            {
                return stream.PlayTokens.Contains(authToken);
            }

            return false;
        }
    }

    /// <summary>
    /// 服务器配置
    /// </summary>
    public class ServerConfiguration
    {
        public int RtmpPort { get; set; } = 1935;
        public int RtspPort { get; set; } = 554;
        public int HlsPort { get; set; } = 8080;
        public Dictionary<string, VirtualHostConfig> VirtualHosts { get; set; } = new();
    }

    /// <summary>
    /// 虚拟主机配置
    /// </summary>
    public class VirtualHostConfig
    {
        public string Name { get; set; } = string.Empty;
        public bool RequireAuth { get; set; } = false;
        public Dictionary<string, string> Users { get; set; } = new(); // username -> password hash
        public Dictionary<string, StreamConfig> Streams { get; set; } = new();
    }

    /// <summary>
    /// 流配置
    /// </summary>
    public class StreamConfig
    {
        public string Key { get; set; } = string.Empty;
        public bool IsPublishing { get; set; } = true;
        public bool IsPlaying { get; set; } = true;
        public bool RequireAuth { get; set; } = false;
        public string? PublisherUser { get; set; }
        public HashSet<string> PublishTokens { get; set; } = new();
        public HashSet<string> PlayTokens { get; set; } = new();
        public int MaxConnections { get; set; } = 100;
        public TimeSpan? MaxDuration { get; set; }
    }
}
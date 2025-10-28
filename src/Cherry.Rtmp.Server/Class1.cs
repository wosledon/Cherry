using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cherry.Media;

namespace Cherry.Rtmp.Server
{
    /// <summary>
    /// RTMP服务器实现
    /// </summary>
    public class RtmpServer
    {
        private TcpListener? _listener;
        private bool _running;
        private CancellationTokenSource? _cts;
        private readonly DefaultServerConfigManager _configManager;
        private readonly Dictionary<string, RtmpStream> _activeStreams = new();

        public event EventHandler<MediaFrame>? StreamFrameReceived;
        public event EventHandler<string>? StreamStarted; // streamKey
        public event EventHandler<string>? StreamStopped; // streamKey

        public RtmpServer(DefaultServerConfigManager configManager)
        {
            _configManager = configManager;
        }

        public async Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Any, _configManager.Config.RtmpPort);
            _listener.Start();
            _running = true;
            _cts = new CancellationTokenSource();

            Console.WriteLine($"RTMP Server started on port {_configManager.Config.RtmpPort}");

            try
            {
                await AcceptClientsAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不记录错误
            }
            finally
            {
                _running = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            while (_running && _listener != null && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException)
                {
                    // 取消操作，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (_running && !cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"Accept client error: {ex.Message}");
                    }
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping listener: {ex.Message}");
            }
            _listener = null;

            // 清理所有活动流
            foreach (var stream in _activeStreams.Values)
            {
                stream.Stop();
            }
            _activeStreams.Clear();
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            Console.WriteLine($"New RTMP client connected from {client.Client.RemoteEndPoint}");

            using var stream = client.GetStream();
            var rtmpConnection = new RtmpConnection(stream, _configManager);

            rtmpConnection.StreamFrameReceived += OnStreamFrameReceived;
            rtmpConnection.StreamStarted += OnStreamStarted;
            rtmpConnection.StreamStopped += OnStreamStopped;

            try
            {
                await rtmpConnection.HandleConnectionAsync();
            }
            catch (IOException ex) when (ex.InnerException is SocketException socketEx &&
                                       (socketEx.SocketErrorCode == SocketError.ConnectionReset ||
                                        socketEx.SocketErrorCode == SocketError.ConnectionAborted))
            {
                // 客户端正常断开连接，不记录为错误
                Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTMP connection error: {ex.Message}");
            }
            finally
            {
                rtmpConnection.StreamFrameReceived -= OnStreamFrameReceived;
                rtmpConnection.StreamStarted -= OnStreamStarted;
                rtmpConnection.StreamStopped -= OnStreamStopped;

                // 清理连接的流
                var streamKey = rtmpConnection.Stream.StreamKey;
                if (!string.IsNullOrEmpty(streamKey) && _activeStreams.ContainsKey(streamKey))
                {
                    _activeStreams.Remove(streamKey);
                    StreamStopped?.Invoke(this, streamKey);
                }
            }
        }

        private void OnStreamFrameReceived(object? sender, MediaFrame frame)
        {
            StreamFrameReceived?.Invoke(this, frame);
        }

        private void OnStreamStarted(object? sender, string streamKey)
        {
            if (sender is RtmpConnection connection)
            {
                _activeStreams[streamKey] = connection.Stream;
            }
            StreamStarted?.Invoke(this, streamKey);
        }

        private void OnStreamStopped(object? sender, string streamKey)
        {
            _activeStreams.Remove(streamKey);
            StreamStopped?.Invoke(this, streamKey);
        }

        /// <summary>
        /// 获取活动流列表
        /// </summary>
        public IEnumerable<string> GetActiveStreams()
        {
            return _activeStreams.Keys;
        }

        /// <summary>
        /// 获取流信息
        /// </summary>
        public RtmpStream? GetStream(string streamKey)
        {
            return _activeStreams.GetValueOrDefault(streamKey);
        }
    }

    /// <summary>
    /// RTMP连接处理
    /// </summary>
    internal class RtmpConnection
    {
        private readonly NetworkStream _stream;
        private readonly DefaultServerConfigManager _configManager;
        private readonly Dictionary<uint, RtmpChunk> _chunks = new();
        private uint _chunkSize = 128;
        private uint _windowSize = 2500000;
        private uint _peerBandwidth = 2500000;
        private byte _limitType = 2;

        public RtmpStream Stream { get; } = new();
        public event EventHandler<MediaFrame>? StreamFrameReceived;
        public event EventHandler<string>? StreamStarted;
        public event EventHandler<string>? StreamStopped;

        public RtmpConnection(NetworkStream stream, DefaultServerConfigManager configManager)
        {
            _stream = stream;
            _configManager = configManager;
        }

        public async Task HandleConnectionAsync()
        {
            // RTMP握手
            if (!await PerformHandshakeAsync())
            {
                return;
            }

            // 处理RTMP消息
            await ProcessMessagesAsync();
        }

        private async Task<bool> PerformHandshakeAsync()
        {
            try
            {
                Console.WriteLine("Starting RTMP handshake...");

                // 读取C0C1
                var c0c1 = new byte[1537];
                int bytesRead = await _stream.ReadAsync(c0c1);
                Console.WriteLine($"Read C0C1: {bytesRead} bytes, version: {c0c1[0]}");

                if (bytesRead != 1537 || c0c1[0] != 3)
                {
                    Console.WriteLine("Invalid C0C1 packet");
                    return false;
                }

                // 发送S0S1S2
                var s0s1s2 = new byte[3073];
                s0s1s2[0] = 3; // RTMP version

                // 复制C1到S1
                Array.Copy(c0c1, 1, s0s1s2, 1, 1536);

                // 生成S2（简单回显C1）
                Array.Copy(c0c1, 1, s0s1s2, 1537, 1536);

                await _stream.WriteAsync(s0s1s2);
                Console.WriteLine("Sent S0S1S2");

                // 读取C2
                var c2 = new byte[1536];
                bytesRead = await _stream.ReadAsync(c2);
                Console.WriteLine($"Read C2: {bytesRead} bytes");

                if (bytesRead != 1536)
                {
                    Console.WriteLine("Invalid C2 packet");
                    return false;
                }

                Console.WriteLine("RTMP handshake completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Handshake error: {ex.Message}");
                return false;
            }
        }

        private async Task ProcessMessagesAsync()
        {
            var buffer = new byte[4096];
            var messageBuffer = new Dictionary<uint, List<byte>>();

            try
            {
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30秒超时

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, timeoutCts.Token);
                    if (bytesRead == 0)
                    {
                        Console.WriteLine("Client closed connection (bytesRead == 0)");
                        break; // 客户端关闭连接
                    }

                    Console.WriteLine($"Received {bytesRead} bytes of RTMP data");

                    // 处理RTMP数据
                    int offset = 0;
                    while (offset < bytesRead)
                    {
                        var (chunk, bytesConsumed) = ParseChunk(buffer.AsSpan(offset, bytesRead - offset));
                        if (chunk == null) 
                        {
                            Console.WriteLine($"Failed to parse chunk at offset {offset}, remaining data: {bytesRead - offset} bytes");
                            break;
                        }

                        offset += bytesConsumed;
                        Console.WriteLine($"Parsed chunk: Type={chunk.MessageType}, Length={chunk.MessageLength}, StreamId={chunk.MessageStreamId}");

                        // 处理消息
                        await ProcessChunkAsync(chunk);
                    }

                    // 检查是否还有未处理的数据
                    if (offset < bytesRead)
                    {
                        Console.WriteLine($"Warning: {bytesRead - offset} bytes of data not processed");
                    }
                }

                if (timeoutCts.Token.IsCancellationRequested)
                {
                    Console.WriteLine("Connection timed out waiting for client data - OBS may have disconnected or stopped sending commands");
                    Console.WriteLine("This could indicate:");
                    Console.WriteLine("1. OBS configuration issue (check RTMP URL: rtmp://localhost:1935/live)");
                    Console.WriteLine("2. Connect response format problem");
                    Console.WriteLine("3. OBS stopped the connection process");
                }
            }
            catch (IOException ex) when (ex.InnerException is SocketException socketEx &&
                                       (socketEx.SocketErrorCode == SocketError.ConnectionReset ||
                                        socketEx.SocketErrorCode == SocketError.ConnectionAborted))
            {
                // 客户端断开连接，正常情况
                Console.WriteLine("Client disconnected during message processing (normal disconnection)");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Connection cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error during message processing: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private (RtmpChunk? chunk, int bytesConsumed) ParseChunk(ReadOnlySpan<byte> data)
        {
            int localOffset = 0;
            if (localOffset + 1 > data.Length) return (null, 0);

            byte firstByte = data[localOffset++];
            byte format = (byte)(firstByte >> 6);
            uint chunkStreamId = (uint)(firstByte & 0x3F);

            Console.WriteLine($"Parsing chunk: firstByte=0x{firstByte:X2}, format={format}, baseChunkStreamId={chunkStreamId}");

            if (chunkStreamId == 0)
            {
                if (localOffset + 1 > data.Length) return (null, 0);
                chunkStreamId = (uint)(data[localOffset++] + 64);
                Console.WriteLine($"Extended chunk stream ID: {chunkStreamId}");
            }
            else if (chunkStreamId == 1)
            {
                if (localOffset + 2 > data.Length) return (null, 0);
                chunkStreamId = (uint)(data[localOffset] + (data[localOffset + 1] << 8) + 64);
                localOffset += 2;
                Console.WriteLine($"Extended chunk stream ID: {chunkStreamId}");
            }

            var chunk = new RtmpChunk
            {
                Format = format,
                ChunkStreamId = chunkStreamId
            };

            // 根据格式读取头信息
            if (!_chunks.ContainsKey(chunkStreamId))
            {
                _chunks[chunkStreamId] = new RtmpChunk();
                Console.WriteLine($"Created new chunk for stream {chunkStreamId}");
            }

            var prevChunk = _chunks[chunkStreamId];

            switch (format)
            {
                case 0: // 11字节头
                    if (localOffset + 11 > data.Length) return (null, 0);
                    chunk.Timestamp = ReadUInt24(data, localOffset);
                    chunk.MessageLength = ReadUInt24(data, localOffset + 3);
                    chunk.MessageType = (RtmpMessageType)data[localOffset + 6];
                    chunk.MessageStreamId = ReadUInt32LittleEndian(data, localOffset + 7);
                    localOffset += 11;
                    Console.WriteLine($"Format 0: Timestamp={chunk.Timestamp}, MessageLength={chunk.MessageLength}, MessageType={chunk.MessageType}, MessageStreamId={chunk.MessageStreamId}");
                    Console.WriteLine($"Raw header data: {BitConverter.ToString(data.Slice(localOffset - 11, 11).ToArray())}");
                    break;
                case 1: // 7字节头
                    if (localOffset + 7 > data.Length) return (null, 0);
                    chunk.Timestamp = ReadUInt24(data, localOffset);
                    chunk.MessageLength = ReadUInt24(data, localOffset + 3);
                    chunk.MessageType = (RtmpMessageType)data[localOffset + 6];
                    chunk.Timestamp += prevChunk.Timestamp;
                    chunk.MessageLength = prevChunk.MessageLength;
                    chunk.MessageStreamId = prevChunk.MessageStreamId;
                    localOffset += 7;
                    Console.WriteLine($"Format 1: Timestamp={chunk.Timestamp}, MessageLength={chunk.MessageLength}, MessageType={chunk.MessageType}, MessageStreamId={chunk.MessageStreamId}");
                    Console.WriteLine($"Raw header data: {BitConverter.ToString(data.Slice(localOffset - 7, 7).ToArray())}");
                    break;
                case 2: // 3字节头
                    if (localOffset + 3 > data.Length) return (null, 0);
                    chunk.Timestamp = ReadUInt24(data, localOffset) + prevChunk.Timestamp;
                    chunk.MessageLength = prevChunk.MessageLength;
                    chunk.MessageType = prevChunk.MessageType;
                    chunk.MessageStreamId = prevChunk.MessageStreamId;
                    localOffset += 3;
                    Console.WriteLine($"Format 2: Timestamp={chunk.Timestamp}, MessageLength={chunk.MessageLength}, MessageType={chunk.MessageType}, MessageStreamId={chunk.MessageStreamId}");
                    Console.WriteLine($"Raw header data: {BitConverter.ToString(data.Slice(localOffset - 3, 3).ToArray())}");
                    break;
                case 3: // 0字节头
                    chunk.Timestamp = prevChunk.Timestamp;
                    chunk.MessageLength = prevChunk.MessageLength;
                    chunk.MessageType = prevChunk.MessageType;
                    chunk.MessageStreamId = prevChunk.MessageStreamId;
                    Console.WriteLine($"Format 3: Timestamp={chunk.Timestamp}, MessageLength={chunk.MessageLength}, MessageType={chunk.MessageType}, MessageStreamId={chunk.MessageStreamId}");
                    break;
            }

            _chunks[chunkStreamId] = chunk;

            // 读取数据
            int dataSize = Math.Min((int)_chunkSize, (int)chunk.MessageLength - chunk.Data.Count);
            Console.WriteLine($"Reading data: chunkSize={_chunkSize}, messageLength={chunk.MessageLength}, currentDataCount={chunk.Data.Count}, dataSize={dataSize}, availableData={data.Length - localOffset}");

            if (localOffset + dataSize > data.Length)
            {
                Console.WriteLine("Not enough data available, waiting for more");
                return (null, 0);
            }

            chunk.Data.AddRange(data.Slice(localOffset, dataSize));
            localOffset += dataSize;

            Console.WriteLine($"Added {dataSize} bytes, total data: {chunk.Data.Count}/{chunk.MessageLength}");

            // 检查是否收到完整消息
            if (chunk.Data.Count >= chunk.MessageLength)
            {
                Console.WriteLine("Complete chunk received");
                return (chunk, localOffset);
            }

            Console.WriteLine("Chunk incomplete, waiting for more data");
            return (null, 0);
        }

        private async Task ProcessChunkAsync(RtmpChunk chunk)
        {
            Console.WriteLine($"Processing chunk: Type={chunk.MessageType}, Length={chunk.MessageLength}");

            switch (chunk.MessageType)
            {
                case RtmpMessageType.SetChunkSize:
                    _chunkSize = ReadUInt32BigEndian(chunk.Data.ToArray());
                    Console.WriteLine($"Set chunk size to {_chunkSize}");
                    break;

                case RtmpMessageType.WindowAcknowledgementSize:
                    _windowSize = ReadUInt32BigEndian(chunk.Data.ToArray());
                    break;

                case RtmpMessageType.SetPeerBandwidth:
                    _peerBandwidth = ReadUInt32BigEndian(chunk.Data.ToArray());
                    _limitType = chunk.Data[4];
                    // 发送Window Acknowledgement Size
                    await SendWindowAcknowledgementSizeAsync();
                    break;

                case RtmpMessageType.Amf0Command:
                    await ProcessCommandAsync(chunk);
                    break;

                case RtmpMessageType.Audio:
                    ProcessAudioData(chunk);
                    break;

                case RtmpMessageType.Video:
                    ProcessVideoData(chunk);
                    break;

                default:
                    Console.WriteLine($"Unhandled message type: {chunk.MessageType}");
                    break;
            }
        }

        private async Task ProcessCommandAsync(RtmpChunk chunk)
        {
            // 解析AMF命令
            var command = ParseAmfCommand(chunk.Data.ToArray());
            if (command == null)
            {
                Console.WriteLine("Failed to parse AMF command");
                return;
            }

            Console.WriteLine($"Received AMF command: {command.CommandName}, TransactionId: {command.TransactionId}");
            Console.WriteLine($"Command properties: {string.Join(", ", command.Properties.Select(p => $"{p.Key}={p.Value}"))}");
            Console.WriteLine($"Command arguments: {string.Join(", ", command.Arguments.Select(a => a?.ToString() ?? "null"))}");

            switch (command.CommandName)
            {
                case "connect":
                    await HandleConnectAsync(command);
                    break;
                case "createStream":
                    await HandleCreateStreamAsync(command);
                    break;
                case "publish":
                    await HandlePublishAsync(command);
                    break;
                case "play":
                    await HandlePlayAsync(command);
                    break;
                default:
                    Console.WriteLine($"Unknown command: {command.CommandName}");
                    break;
            }
        }

        private async Task HandleConnectAsync(AmfCommand command)
        {
            Console.WriteLine("Handling connect command");

            // 检查app参数，如果为空则设置为默认值
            string app = command.Properties.GetValueOrDefault("app", "").ToString() ?? "";
            if (string.IsNullOrEmpty(app))
            {
                app = "live"; // 默认应用名称
                Console.WriteLine($"Empty app parameter, using default: {app}");
            }

            // 存储应用信息
            Stream.AppName = app;

            // 发送控制消息（在connect响应之前）
            await SendWindowAcknowledgementSizeAsync();
            Console.WriteLine("Window acknowledgement size sent");

            await SendSetPeerBandwidthAsync();
            Console.WriteLine("Set peer bandwidth sent");

            // 注意：StreamBegin将在publish开始时发送

            // 发送_connect响应
            var response = new AmfCommand
            {
                CommandName = "_result",
                TransactionId = command.TransactionId,
                Properties = new Dictionary<string, object>
                {
                    ["fmsVer"] = "FMS/3.5.1",
                    ["capabilities"] = 239.0,
                    ["objectEncoding"] = 0.0,
                    ["app"] = app // 包含app参数
                },
                Info = new Dictionary<string, object>
                {
                    ["level"] = "status",
                    ["code"] = "NetConnection.Connect.Success",
                    ["description"] = "Connection succeeded."
                }
            };

            Console.WriteLine("Sending connect response");
            await SendCommandAsync(response);
            Console.WriteLine("Connect response sent");
        }

        private async Task HandleCreateStreamAsync(AmfCommand command)
        {
            Console.WriteLine("Handling createStream command");

            var response = new AmfCommand
            {
                CommandName = "_result",
                TransactionId = command.TransactionId,
                StreamId = 1
            };

            Console.WriteLine("Sending createStream response");
            await SendCommandAsync(response);
            Console.WriteLine("CreateStream response sent");
        }

        private async Task HandlePublishAsync(AmfCommand command)
        {
            if (command.Arguments.Count < 2) return;

            string publishingName = command.Arguments[1]?.ToString() ?? "";
            string publishingType = command.Arguments.Count > 2 ? command.Arguments[2]?.ToString() ?? "" : "";

            Console.WriteLine($"Handling publish command: name={publishingName}, type={publishingType}");

            // 检查授权
            if (!ValidatePublishAuth(publishingName))
            {
                Console.WriteLine($"Publish authorization failed for: {publishingName}");
                var errorResponse = new AmfCommand
                {
                    CommandName = "onStatus",
                    TransactionId = 0,
                    Properties = new Dictionary<string, object>
                    {
                        ["level"] = "error",
                        ["code"] = "NetStream.Publish.BadName",
                        ["description"] = "Authorization failed."
                    }
                };
                await SendCommandAsync(errorResponse);
                return;
            }

            Console.WriteLine($"Publish authorized for: {publishingName}");

            Stream.StreamKey = publishingName;
            Stream.IsPublishing = true;

            var response = new AmfCommand
            {
                CommandName = "onStatus",
                TransactionId = 0,
                Properties = new Dictionary<string, object>
                {
                    ["level"] = "status",
                    ["code"] = "NetStream.Publish.Start",
                    ["description"] = "Publishing started."
                }
            };

            Console.WriteLine("Sending publish success response");
            await SendCommandAsync(response);
            Console.WriteLine($"Publish started for: {publishingName} - OBS should now start streaming!");

            // 发送stream begin user control message
            await SendStreamBeginAsync();
            Console.WriteLine("Stream begin sent for publishing");

            StreamStarted?.Invoke(this, publishingName);
        }

        private async Task HandlePlayAsync(AmfCommand command)
        {
            if (command.Arguments.Count < 1) return;

            string streamName = command.Arguments[0]?.ToString() ?? "";

            var response = new AmfCommand
            {
                CommandName = "onStatus",
                TransactionId = 0,
                Properties = new Dictionary<string, object>
                {
                    ["level"] = "status",
                    ["code"] = "NetStream.Play.Start",
                    ["description"] = "Playing started."
                }
            };

            await SendCommandAsync(response);
        }

        private bool ValidatePublishAuth(string streamKey)
        {
            // 使用配置管理器验证发布权限
            return _configManager.ValidatePublishAuth("default", streamKey);
        }

        private void ProcessAudioData(RtmpChunk chunk)
        {
            var frame = new MediaFrame
            {
                Type = MediaFrameType.Audio,
                Timestamp = chunk.Timestamp,
                Data = chunk.Data.ToArray()
            };

            // 解析音频格式
            if (chunk.Data.Count > 0)
            {
                byte format = (byte)(chunk.Data[0] >> 4);
                frame.Codec = format switch
                {
                    10 => CodecType.AAC,
                    2 => CodecType.MP3,
                    _ => CodecType.Unknown
                };
            }

            StreamFrameReceived?.Invoke(this, frame);
        }

        private void ProcessVideoData(RtmpChunk chunk)
        {
            var frame = new MediaFrame
            {
                Type = MediaFrameType.Video,
                Timestamp = chunk.Timestamp,
                Data = chunk.Data.ToArray()
            };

            // 解析视频格式
            if (chunk.Data.Count > 0)
            {
                byte frameType = (byte)(chunk.Data[0] >> 4);
                byte codecId = (byte)(chunk.Data[0] & 0x0F);

                frame.IsKeyFrame = (frameType & 0x01) == 1;
                frame.Codec = codecId switch
                {
                    7 => CodecType.H264,
                    12 => CodecType.H265,
                    _ => CodecType.Unknown
                };
            }

            StreamFrameReceived?.Invoke(this, frame);
        }

        private async Task SendCommandAsync(AmfCommand command)
        {
            Console.WriteLine($"Sending AMF command: {command.CommandName}, TransactionId: {command.TransactionId}");

            // 实现AMF编码和发送
            var data = EncodeAmfCommand(command);
            Console.WriteLine($"Encoded AMF command data: {BitConverter.ToString(data)}");

            var chunk = new RtmpChunk
            {
                Format = 0,
                ChunkStreamId = 3,
                Timestamp = 0,
                MessageLength = (uint)data.Length,
                MessageType = RtmpMessageType.Amf0Command,
                MessageStreamId = 0,
                Data = new List<byte>(data)
            };

            await SendChunkAsync(chunk);
            Console.WriteLine($"Sent AMF command: {command.CommandName}");
        }

        private async Task SendWindowAcknowledgementSizeAsync()
        {
            var data = BitConverter.GetBytes(_windowSize);
            Array.Reverse(data); // Big endian

            var chunk = new RtmpChunk
            {
                Format = 0,
                ChunkStreamId = 2,
                Timestamp = 0,
                MessageLength = 4,
                MessageType = RtmpMessageType.WindowAcknowledgementSize,
                MessageStreamId = 0,
                Data = new List<byte>(data)
            };

            await SendChunkAsync(chunk);
        }

        private async Task SendSetPeerBandwidthAsync()
        {
            var data = new List<byte>();
            // Bandwidth (4 bytes, big endian)
            var bandwidth = BitConverter.GetBytes(2500000); // 2.5 Mbps
            Array.Reverse(bandwidth);
            data.AddRange(bandwidth);
            // Limit type (1 byte) - 2 = dynamic
            data.Add(2);

            var chunk = new RtmpChunk
            {
                Format = 0,
                ChunkStreamId = 2,
                Timestamp = 0,
                MessageLength = 5,
                MessageType = RtmpMessageType.SetPeerBandwidth,
                MessageStreamId = 0,
                Data = data
            };

            await SendChunkAsync(chunk);
        }

        private async Task SendStreamBeginAsync()
        {
            var data = new List<byte>();
            // Event type (2 bytes, big endian) - 0 = Stream Begin
            data.AddRange(new byte[] { 0x00, 0x00 });
            // Stream ID (4 bytes, big endian) - 0
            data.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });

            var chunk = new RtmpChunk
            {
                Format = 0,
                ChunkStreamId = 2,
                Timestamp = 0,
                MessageLength = 6,
                MessageType = RtmpMessageType.UserControl,
                MessageStreamId = 0,
                Data = data
            };

            await SendChunkAsync(chunk);
        }

        private async Task SendChunkAsync(RtmpChunk chunk)
        {
            Console.WriteLine($"Sending chunk: Type={chunk.MessageType}, Length={chunk.MessageLength}, StreamId={chunk.MessageStreamId}");
            var data = EncodeChunk(chunk);
            await _stream.WriteAsync(data);
            Console.WriteLine($"Sent {data.Length} bytes to client");
        }

        private byte[] EncodeChunk(RtmpChunk chunk)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Chunk header
            byte firstByte = (byte)((chunk.Format << 6) | (chunk.ChunkStreamId < 64 ? (byte)chunk.ChunkStreamId : (byte)0));
            writer.Write(firstByte);

            if (chunk.ChunkStreamId >= 64)
            {
                if (chunk.ChunkStreamId < 320)
                {
                    writer.Write((byte)(chunk.ChunkStreamId - 64));
                }
                else
                {
                    writer.Write((byte)((chunk.ChunkStreamId - 64) & 0xFF));
                    writer.Write((byte)((chunk.ChunkStreamId - 64) >> 8));
                }
            }

            // 根据格式写入头信息
            switch (chunk.Format)
            {
                case 0:
                    WriteUInt24(writer, chunk.Timestamp);
                    WriteUInt24(writer, chunk.MessageLength);
                    writer.Write((byte)chunk.MessageType);
                    WriteUInt32LittleEndian(writer, chunk.MessageStreamId);
                    break;
                case 1:
                    WriteUInt24(writer, chunk.Timestamp);
                    WriteUInt24(writer, chunk.MessageLength);
                    writer.Write((byte)chunk.MessageType);
                    break;
                case 2:
                    WriteUInt24(writer, chunk.Timestamp);
                    break;
            }

            // 写入数据
            writer.Write(chunk.Data.ToArray());

            return ms.ToArray();
        }

        // AMF解析辅助方法
        private string? ReadAmfString(byte[] data, ref int offset)
        {
            if (offset + 3 > data.Length) return null;

            // AMF0 string marker
            if (data[offset] != 0x02) return null;
            offset++;

            // String length
            int length = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (offset + length > data.Length) return null;

            var str = System.Text.Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return str;
        }

        private string? ReadAmfStringValue(byte[] data, ref int offset)
        {
            if (offset + 2 > data.Length) return null;

            // String length (type marker already consumed)
            int length = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (offset + length > data.Length) return null;

            var str = System.Text.Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return str;
        }

        private double? ReadAmfNumber(byte[] data, ref int offset)
        {
            if (offset + 9 > data.Length) return null;

            // AMF0 number marker
            if (data[offset] != 0x00) return null;
            offset++;

            // Read double (big endian)
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data, offset, 8);
            }
            double value = BitConverter.ToDouble(data, offset);
            offset += 8;

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data, offset - 8, 8);
            }

            return value;
        }

        private Dictionary<string, object>? ReadAmfObject(byte[] data, ref int offset)
        {
            if (offset >= data.Length) return null;

            // AMF0 object marker
            if (data[offset] != 0x03)
            {
                Console.WriteLine($"Expected AMF object marker 0x03, got 0x{data[offset]:X2}");
                return null;
            }
            offset++;

            var obj = new Dictionary<string, object>();

            while (offset + 2 < data.Length)
            {
                // Check for end marker
                if (data[offset] == 0x00 && data[offset + 1] == 0x00 && data[offset + 2] == 0x09)
                {
                    offset += 3;
                    break;
                }

                var key = ReadAmfPropertyName(data, ref offset);
                if (key == null) break;

                var value = ReadAmfValue(data, ref offset);
                if (value != null)
                {
                    obj[key] = value;
                }
            }

            return obj;
        }

        private string? ReadAmfPropertyName(byte[] data, ref int offset)
        {
            if (offset + 2 > data.Length) return null;

            // Property name length (2 bytes, big-endian)
            int length = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (offset + length > data.Length) return null;

            var str = System.Text.Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return str;
        }

        private object? ReadAmfValue(byte[] data, ref int offset)
        {
            if (offset >= data.Length) return null;

            byte type = data[offset++];
            switch (type)
            {
                case 0x00: // number
                    return ReadAmfNumber(data, ref offset);
                case 0x02: // string
                    return ReadAmfStringValue(data, ref offset);
                case 0x03: // object
                    return ReadAmfObject(data, ref offset);
                default:
                    return null; // Unsupported type
            }
        }

        private AmfCommand? ParseAmfCommand(byte[] data)
        {
            try
            {
                int offset = 0;
                Console.WriteLine($"Parsing AMF command, data length: {data.Length}");
                Console.WriteLine($"AMF data: {BitConverter.ToString(data)}");

                // 读取命令名
                if (offset >= data.Length) return null;
                var commandName = ReadAmfString(data, ref offset);
                if (commandName == null) return null;
                Console.WriteLine($"Command name: {commandName}, offset after name: {offset}");

                // 读取事务ID
                if (offset >= data.Length) return null;
                var transactionId = ReadAmfNumber(data, ref offset);
                Console.WriteLine($"Transaction ID: {transactionId}, offset after transactionId: {offset}");

                // 读取命令对象
                var properties = ReadAmfObject(data, ref offset);
                Console.WriteLine($"Properties count: {properties?.Count ?? 0}, offset after properties: {offset}");

                // 读取其他参数
                var arguments = new List<object?>();
                while (offset < data.Length)
                {
                    var arg = ReadAmfValue(data, ref offset);
                    arguments.Add(arg);
                    Console.WriteLine($"Read argument: {arg}, offset: {offset}");
                }

                Console.WriteLine($"Total arguments: {arguments.Count}");

                return new AmfCommand
                {
                    CommandName = commandName,
                    TransactionId = transactionId ?? 0,
                    Properties = properties ?? new Dictionary<string, object>(),
                    Info = properties ?? new Dictionary<string, object>(),
                    Arguments = arguments
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing AMF command: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        private byte[] EncodeAmfCommand(AmfCommand command)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Command name
            WriteAmfString(writer, command.CommandName);

            // Transaction ID
            WriteAmfNumber(writer, command.TransactionId);

            // Properties object (for connect _result)
            if (command.Properties.Count > 0)
            {
                WriteAmfObject(writer, command.Properties);
            }
            else
            {
                WriteAmfNull(writer);
            }

            // Info object (for connect _result and onStatus)
            if (command.Info.Count > 0)
            {
                WriteAmfObject(writer, command.Info);
            }
            else
            {
                WriteAmfNull(writer);
            }

            // Additional arguments
            foreach (var arg in command.Arguments)
            {
                WriteAmfValue(writer, arg);
            }

            return ms.ToArray();
        }

        private void WriteAmfString(BinaryWriter writer, string value)
        {
            writer.Write((byte)0x02); // AMF0 string marker
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((byte)(bytes.Length >> 8));
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private void WriteAmfNumber(BinaryWriter writer, double value)
        {
            writer.Write((byte)0x00); // AMF0 number marker
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            writer.Write(bytes);
        }

        private void WriteAmfObject(BinaryWriter writer, Dictionary<string, object> obj)
        {
            writer.Write((byte)0x03); // AMF0 object marker
            foreach (var kvp in obj)
            {
                WriteAmfString(writer, kvp.Key);
                WriteAmfValue(writer, kvp.Value);
            }
            // End marker
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x09);
        }

        private void WriteAmfNull(BinaryWriter writer)
        {
            writer.Write((byte)0x05); // AMF0 null marker
        }

        private void WriteAmfValue(BinaryWriter writer, object? value)
        {
            if (value == null)
            {
                WriteAmfNull(writer);
            }
            else if (value is string str)
            {
                WriteAmfString(writer, str);
            }
            else if (value is double num)
            {
                WriteAmfNumber(writer, num);
            }
            else
            {
                WriteAmfNull(writer); // Default to null for unsupported types
            }
        }

        private uint ReadUInt24(ReadOnlySpan<byte> data, int offset = 0)
        {
            return (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
        }

        private uint ReadUInt32BigEndian(byte[] data, int offset = 0)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        private uint ReadUInt32LittleEndian(ReadOnlySpan<byte> data, int offset = 0)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private void WriteUInt24(BinaryWriter writer, uint value)
        {
            writer.Write((byte)value);
            writer.Write((byte)(value >> 8));
            writer.Write((byte)(value >> 16));
        }

        private void WriteUInt32LittleEndian(BinaryWriter writer, uint value)
        {
            writer.Write((byte)value);
            writer.Write((byte)(value >> 8));
            writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 24));
        }
    }

    /// <summary>
    /// RTMP块
    /// </summary>
    internal class RtmpChunk
    {
        public byte Format { get; set; }
        public uint ChunkStreamId { get; set; }
        public uint Timestamp { get; set; }
        public uint MessageLength { get; set; }
        public RtmpMessageType MessageType { get; set; }
        public uint MessageStreamId { get; set; }
        public List<byte> Data { get; set; } = new();
    }

    /// <summary>
    /// AMF命令
    /// </summary>
    internal class AmfCommand
    {
        public string CommandName { get; set; } = string.Empty;
        public double TransactionId { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
        public Dictionary<string, object> Info { get; set; } = new();
        public List<object?> Arguments { get; set; } = new();
        public double StreamId { get; set; }
    }

    /// <summary>
    /// RTMP流
    /// </summary>
    public class RtmpStream
    {
        public string AppName { get; set; } = string.Empty;
        public string StreamKey { get; set; } = string.Empty;
        public bool IsPublishing { get; set; }
        public bool IsPlaying { get; set; }
        public MediaStream StreamInfo { get; } = new();

        public void Stop()
        {
            IsPublishing = false;
            IsPlaying = false;
        }
    }
}

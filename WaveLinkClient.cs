using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace InfoPanel.AudioSpectrum
{
    /// <summary>
    /// Connects to Elgato Wave Link's WebSocket API to discover the virtual
    /// audio channel names. The plugin captures from all channels simultaneously
    /// to recreate the personal mix for spectrum visualization.
    ///
    /// Also tracks the currently selected monitor output device name for display.
    ///
    /// Supports Wave Link 3.x. Port is read from ws-info.json; connection uses
    /// origin header "streamdeck://" and JSON-RPC 2.0 protocol.
    /// </summary>
    internal sealed class WaveLinkClient : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<WaveLinkClient>();

        private const string WS_INFO_RELATIVE_PATH =
            @"Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\ws-info.json";
        private const string ORIGIN = "streamdeck://";
        private const int RECONNECT_DELAY_MS = 5000;
        private const int POLL_INTERVAL_MS = 10000;
        private const int RECEIVE_BUFFER_SIZE = 65536;

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private Thread? _thread;
        private volatile bool _running;
        private int _nextId = 1;
        private string[]? _lastChannelNames;
        private string? _lastOutputDevice;

        /// <summary>
        /// Fires when Wave Link channel list is first discovered.
        /// Argument: array of channel names for WASAPI loopback capture.
        /// </summary>
        public event Action<string[]>? ChannelsDiscovered;

        /// <summary>
        /// Fires when the main output device changes (for display purposes).
        /// Argument: the output device name.
        /// </summary>
        public event Action<string>? OutputDeviceChanged;

        /// <summary>
        /// The currently selected main output device name, or null if unknown.
        /// </summary>
        public string? CurrentOutputDeviceName { get; private set; }

        /// <summary>
        /// Whether the client is connected to Wave Link.
        /// </summary>
        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "WaveLink-Client"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _cts?.Cancel();
            try { _ws?.Abort(); } catch { }
            _thread?.Join(3000);
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            _ws?.Dispose();
            _cts?.Dispose();
        }

        private void RunLoop()
        {
            while (_running)
            {
                try
                {
                    var port = ReadPort();
                    if (port <= 0)
                    {
                        Logger.Debug("Wave Link ws-info.json not found or invalid, retrying...");
                        Thread.Sleep(RECONNECT_DELAY_MS);
                        continue;
                    }

                    Connect(port);
                }
                catch (OperationCanceledException) when (!_running)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug("Wave Link connection error: {Error}", ex.Message);
                }

                if (_running)
                {
                    Thread.Sleep(RECONNECT_DELAY_MS);
                }
            }
        }

        private void Connect(int port)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", ORIGIN);
            _ws = ws;

            var token = _cts?.Token ?? CancellationToken.None;
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), token)
                .GetAwaiter().GetResult();

            Logger.Information("Connected to Wave Link on port {Port}", port);

            // Query channels and output devices
            SendJsonRpc(ws, "getOutputDevices", token);
            SendJsonRpc(ws, "getChannels", token);

            // Message loop
            var buffer = new byte[RECEIVE_BUFFER_SIZE];
            var lastPoll = DateTime.UtcNow;
            var channelNames = Array.Empty<string>();
            string? outputDevice = null;

            while (_running && ws.State == WebSocketState.Open)
            {
                try
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var result = ws.ReceiveAsync(segment, token)
                        .GetAwaiter().GetResult();

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleMessage(json, ref channelNames, ref outputDevice);

                        // Fire channels event only when channel list actually changes
                        if (channelNames.Length > 0 && !ChannelsEqual(channelNames, _lastChannelNames))
                        {
                            _lastChannelNames = channelNames;
                            ChannelsDiscovered?.Invoke(channelNames);
                        }

                        // Fire output device event when it changes
                        if (outputDevice != null && outputDevice != _lastOutputDevice)
                        {
                            _lastOutputDevice = outputDevice;
                            CurrentOutputDeviceName = outputDevice;
                            OutputDeviceChanged?.Invoke(outputDevice);
                        }
                    }

                    // Periodic poll for output device changes
                    if ((DateTime.UtcNow - lastPoll).TotalMilliseconds >= POLL_INTERVAL_MS)
                    {
                        lastPoll = DateTime.UtcNow;
                        SendJsonRpc(ws, "getOutputDevices", token);
                    }
                }
                catch (OperationCanceledException) when (!_running)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }
            }

            Logger.Information("Wave Link connection closed");
        }

        private void SendJsonRpc(ClientWebSocket ws, string method, CancellationToken token)
        {
            try
            {
                var id = Interlocked.Increment(ref _nextId);
                var request = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method
                });

                var bytes = Encoding.UTF8.GetBytes(request);
                ws.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Debug("Failed to send {Method}: {Error}", method, ex.Message);
            }
        }

        private void HandleMessage(string json, ref string[] channelNames, ref string? outputDevice)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Skip events (levelMeterChanged, etc.)
                if (root.TryGetProperty("method", out var method))
                {
                    var methodName = method.GetString();

                    // Output device switched - re-query
                    if (methodName == "mainOutputDeviceChanged" && _ws?.State == WebSocketState.Open)
                    {
                        SendJsonRpc(_ws, "getOutputDevices", _cts?.Token ?? CancellationToken.None);
                    }
                    return;
                }

                if (!root.TryGetProperty("result", out var result))
                    return;

                // Response to getOutputDevices
                if (result.TryGetProperty("outputDevices", out var devices) &&
                    result.TryGetProperty("mainOutput", out var mainOutput))
                {
                    var mainId = mainOutput.GetProperty("outputDeviceId").GetString();
                    foreach (var device in devices.EnumerateArray())
                    {
                        if (device.GetProperty("id").GetString() == mainId)
                        {
                            outputDevice = device.GetProperty("name").GetString();
                            break;
                        }
                    }
                }

                // Response to getChannels - contains the actual channel list
                if (result.TryGetProperty("channels", out var channels))
                {
                    var names = new List<string>();
                    foreach (var channel in channels.EnumerateArray())
                    {
                        var name = channel.GetProperty("name").GetString();
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
                    if (names.Count > 0)
                    {
                        channelNames = names.ToArray();
                        Logger.Information("Wave Link channels: {Channels}", string.Join(", ", names));
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed messages
            }
        }

        private static bool ChannelsEqual(string[] a, string[]? b)
        {
            if (b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int ReadPort()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                var wsInfoPath = Path.Combine(localAppData, WS_INFO_RELATIVE_PATH);

                if (!File.Exists(wsInfoPath))
                    return -1;

                var json = File.ReadAllText(wsInfoPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("port", out var portElement))
                {
                    var port = portElement.GetInt32();
                    return port > 0 ? port : -1;
                }
            }
            catch { }
            return -1;
        }
    }
}

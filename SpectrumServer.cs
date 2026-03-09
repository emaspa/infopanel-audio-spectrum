using System.Net;
using Serilog;

namespace InfoPanel.AudioSpectrum
{
    internal sealed class SpectrumServer : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<SpectrumServer>();

        private volatile byte[]? _imageData;
        private volatile int _frameVersion;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenTask;
        private int _actualPort;
        private bool _disposed;

        private readonly int _requestedPort;
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;

        public SpectrumServer(int port, int width, int height, int fps = 15)
        {
            _requestedPort = port;
            _width = width;
            _height = height;
            _fps = fps;
        }

        public void SetImageData(byte[]? data)
        {
            _imageData = data;
            Interlocked.Increment(ref _frameVersion);
        }

        public string? ImageUrl => _listener?.IsListening == true
            ? $"http://localhost:{_actualPort}/spectrum.avi"
            : null;

        public void Start()
        {
            _cts = new CancellationTokenSource();

            int[] ports = [_requestedPort, _requestedPort + 1, _requestedPort + 2, 0];

            foreach (var port in ports)
            {
                try
                {
                    _listener = new HttpListener();
                    _actualPort = port == 0 ? new Random().Next(49152, 65535) : port;
                    _listener.Prefixes.Add($"http://localhost:{_actualPort}/");
                    _listener.Start();

                    _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
                    Logger.Information("Spectrum server started on port {Port}", _actualPort);
                    return;
                }
                catch
                {
                    _listener?.Close();
                    _listener = null;
                }
            }

            Logger.Error("Failed to start spectrum server on any port");
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(ct);
                    _ = Task.Run(() => HandleRequest(context, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Spectrum server listener error");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context, CancellationToken ct)
        {
            var path = context.Request.Url?.AbsolutePath ?? "";

            if (path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
            {
                HandleAviStream(context, ct);
            }
            else
            {
                HandleSingleFrame(context);
            }
        }

        private void HandleAviStream(HttpListenerContext context, CancellationToken ct)
        {
            int frameIntervalMs = 1000 / _fps;

            try
            {
                context.Response.ContentType = "video/avi";
                context.Response.Headers.Add("Cache-Control", "no-cache, no-store");
                context.Response.SendChunked = true;

                var stream = context.Response.OutputStream;

                // Write AVI header
                WriteAviHeader(stream, _width, _height, _fps);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long nextFrameMs = 0;

                while (!ct.IsCancellationRequested)
                {
                    var now = sw.ElapsedMilliseconds;
                    if (now < nextFrameMs)
                    {
                        Thread.Sleep(Math.Max(1, (int)(nextFrameMs - now)));
                        continue;
                    }
                    nextFrameMs = now + frameIntervalMs;

                    var data = _imageData;
                    if (data == null || data.Length == 0) continue;

                    // Write AVI video chunk: '00dc' + size + JPEG data + padding
                    WriteAviFrame(stream, data);
                }
            }
            catch { }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static void WriteAviHeader(Stream stream, int width, int height, int fps)
        {
            using var ms = new MemoryStream(1024);
            using var w = new BinaryWriter(ms);

            int usPerFrame = 1_000_000 / fps;

            // We write a minimal AVI header. For streaming, we set totalFrames=0
            // and rely on FFmpeg reading frames until EOF.

            // --- hdrl LIST ---
            var hdrl = new MemoryStream();
            var hw = new BinaryWriter(hdrl);

            // avih (AVI main header) - 56 bytes
            hw.Write(FourCC("avih"));
            hw.Write(56); // size
            hw.Write(usPerFrame); // dwMicroSecPerFrame
            hw.Write(0); // dwMaxBytesPerSec
            hw.Write(0); // dwPaddingGranularity
            hw.Write(0); // dwFlags
            hw.Write(0); // dwTotalFrames (0 = streaming)
            hw.Write(0); // dwInitialFrames
            hw.Write(1); // dwStreams
            hw.Write(1024 * 1024); // dwSuggestedBufferSize
            hw.Write(width);
            hw.Write(height);
            hw.Write(0); hw.Write(0); hw.Write(0); hw.Write(0); // dwReserved[4]

            // --- strl LIST (stream header + format) ---
            var strl = new MemoryStream();
            var sw2 = new BinaryWriter(strl);

            // strh (stream header) - 56 bytes
            sw2.Write(FourCC("strh"));
            sw2.Write(56); // size
            sw2.Write(FourCC("vids")); // fccType
            sw2.Write(FourCC("MJPG")); // fccHandler
            sw2.Write(0); // dwFlags
            sw2.Write((short)0); // wPriority
            sw2.Write((short)0); // wLanguage
            sw2.Write(0); // dwInitialFrames
            sw2.Write(1); // dwScale
            sw2.Write(fps); // dwRate
            sw2.Write(0); // dwStart
            sw2.Write(0); // dwLength (0 = streaming)
            sw2.Write(1024 * 1024); // dwSuggestedBufferSize
            sw2.Write(-1); // dwQuality
            sw2.Write(0); // dwSampleSize
            sw2.Write((short)0); sw2.Write((short)0); // rcFrame left, top
            sw2.Write((short)width); sw2.Write((short)height); // rcFrame right, bottom

            // strf (stream format - BITMAPINFOHEADER) - 40 bytes
            sw2.Write(FourCC("strf"));
            sw2.Write(40); // size
            sw2.Write(40); // biSize
            sw2.Write(width);
            sw2.Write(height);
            sw2.Write((short)1); // biPlanes
            sw2.Write((short)24); // biBitCount
            sw2.Write(FourCC("MJPG")); // biCompression
            sw2.Write(width * height * 3); // biSizeImage
            sw2.Write(0); // biXPelsPerMeter
            sw2.Write(0); // biYPelsPerMeter
            sw2.Write(0); // biClrUsed
            sw2.Write(0); // biClrImportant

            byte[] strlData = strl.ToArray();
            byte[] hdrlContent;

            // Write strl LIST into hdrl
            using (var hdrl2 = new MemoryStream())
            {
                var hw2 = new BinaryWriter(hdrl2);
                // avih chunk
                hw2.Write(hdrl.ToArray());
                // strl LIST
                hw2.Write(FourCC("LIST"));
                hw2.Write(strlData.Length + 4); // size includes 'strl' fourcc
                hw2.Write(FourCC("strl"));
                hw2.Write(strlData);
                hdrlContent = hdrl2.ToArray();
            }

            // RIFF header
            w.Write(FourCC("RIFF"));
            w.Write(0); // file size placeholder (0 for streaming)
            w.Write(FourCC("AVI "));

            // hdrl LIST
            w.Write(FourCC("LIST"));
            w.Write(hdrlContent.Length + 4);
            w.Write(FourCC("hdrl"));
            w.Write(hdrlContent);

            // movi LIST header (open-ended for streaming)
            w.Write(FourCC("LIST"));
            w.Write(0); // size placeholder (0 for streaming)
            w.Write(FourCC("movi"));

            var headerBytes = ms.ToArray();
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();
        }

        private static void WriteAviFrame(Stream stream, byte[] jpegData)
        {
            int paddedSize = (jpegData.Length + 1) & ~1; // AVI chunks must be 2-byte aligned

            // '00dc' = video data chunk
            var header = new byte[8];
            header[0] = (byte)'0'; header[1] = (byte)'0'; header[2] = (byte)'d'; header[3] = (byte)'c';
            BitConverter.GetBytes(jpegData.Length).CopyTo(header, 4);

            stream.Write(header, 0, 8);
            stream.Write(jpegData, 0, jpegData.Length);

            // Padding byte if odd size
            if (jpegData.Length != paddedSize)
            {
                stream.WriteByte(0);
            }

            stream.Flush();
        }

        private static int FourCC(string s)
        {
            return s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24);
        }

        private void HandleSingleFrame(HttpListenerContext context)
        {
            try
            {
                var data = _imageData;
                if (data != null)
                {
                    context.Response.ContentType = "image/jpeg";
                    context.Response.ContentLength64 = data.Length;
                    context.Response.Headers.Add("Cache-Control", "no-cache, no-store");
                    context.Response.OutputStream.Write(data, 0, data.Length);
                }
                else
                {
                    context.Response.StatusCode = 204;
                }
            }
            catch { }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts?.Cancel();

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            _cts?.Dispose();
        }
    }
}

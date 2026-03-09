using Serilog;

namespace InfoPanel.AudioSpectrum
{
    internal class AudioCapture : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<AudioCapture>();

        private WasapiLoopback? _loopback;
        private bool _disposed;
        private string _lastError = "";

        public int SampleRate => _loopback?.SampleRate ?? 0;
        public bool IsCapturing => _loopback != null;
        public long DataReceivedCount => _loopback?.DataCount ?? 0;
        public float PeakLevel => _loopback?.PeakLevel ?? 0;
        public string DeviceName => _loopback?.DeviceName ?? "None";
        public string LastError => _lastError;

        internal static void WriteErrorLog(string message)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InfoPanel", "audiospectrum-error.log");
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {message}\n");
            }
            catch { }
        }

        public void Start(string? deviceName = null)
        {
            Stop();

            try
            {
                _loopback = new WasapiLoopback();
                _loopback.Start(deviceName);

                if (_loopback.Error != null)
                {
                    _lastError = _loopback.Error;
                }
            }
            catch (Exception ex)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                WriteErrorLog($"AudioCapture.Start: {ex}");
                _loopback?.Dispose();
                _loopback = null;
            }
        }

        public void Stop()
        {
            _loopback?.Dispose();
            _loopback = null;
        }

        public float[] GetLatestSamples()
        {
            return _loopback?.GetLatestSamples() ?? [];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
            }
        }
    }
}

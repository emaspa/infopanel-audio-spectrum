using Serilog;

namespace InfoPanel.AudioSpectrum
{
    internal class AudioCapture : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<AudioCapture>();

        private WasapiLoopback? _loopback;
        private WasapiLoopback[]? _multiLoopbacks;
        private bool _disposed;
        private string _lastError = "";

        public int SampleRate => _loopback?.SampleRate ?? (_multiLoopbacks?.FirstOrDefault(l => l.SampleRate > 0)?.SampleRate ?? 0);
        public bool IsCapturing => _loopback != null || _multiLoopbacks != null;
        public long DataReceivedCount => _loopback?.DataCount ?? (_multiLoopbacks?.Sum(l => l.DataCount) ?? 0);
        public float PeakLevel => _loopback?.PeakLevel ?? (_multiLoopbacks?.Max(l => l.PeakLevel) ?? 0);
        public string DeviceName => _loopback?.DeviceName ?? (_multiLoopbacks != null ? "Wave Link Mix" : "None");
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

        /// <summary>
        /// Start capturing from multiple devices simultaneously and mixing their audio.
        /// Used for Wave Link integration to capture all virtual channels at once.
        /// </summary>
        public void StartMulti(string[] deviceNames)
        {
            Stop();
            _lastError = "";

            var loopbacks = new List<WasapiLoopback>();
            foreach (var name in deviceNames)
            {
                try
                {
                    var lb = new WasapiLoopback();
                    lb.Start(name);
                    if (lb.Error != null)
                    {
                        WriteErrorLog($"MultiCapture skip {name}: {lb.Error}");
                        lb.Dispose();
                        continue;
                    }
                    loopbacks.Add(lb);
                }
                catch (Exception ex)
                {
                    WriteErrorLog($"MultiCapture skip {name}: {ex.Message}");
                }
            }

            if (loopbacks.Count > 0)
            {
                _multiLoopbacks = loopbacks.ToArray();
                var names = string.Join(", ", _multiLoopbacks.Select(l => l.DeviceName));
                WriteErrorLog($"MultiCapture OK: {loopbacks.Count} devices ({names})");
            }
            else
            {
                _lastError = "No Wave Link devices found";
                WriteErrorLog($"MultiCapture: {_lastError}");
            }
        }

        public void Stop()
        {
            _loopback?.Dispose();
            _loopback = null;

            if (_multiLoopbacks != null)
            {
                foreach (var lb in _multiLoopbacks)
                    lb.Dispose();
                _multiLoopbacks = null;
            }
        }

        public float[] GetLatestSamples()
        {
            if (_loopback != null)
                return _loopback.GetLatestSamples();

            if (_multiLoopbacks != null)
                return GetMixedSamples();

            return [];
        }

        private float[] GetMixedSamples()
        {
            var loopbacks = _multiLoopbacks;
            if (loopbacks == null || loopbacks.Length == 0) return [];

            // Get samples from all devices
            float[]?[] allSamples = new float[loopbacks.Length][];
            int maxLen = 0;
            for (int i = 0; i < loopbacks.Length; i++)
            {
                allSamples[i] = loopbacks[i].GetLatestSamples();
                if (allSamples[i].Length > maxLen)
                    maxLen = allSamples[i].Length;
            }

            if (maxLen == 0) return [];

            // Sum all channels
            var mixed = new float[maxLen];
            for (int i = 0; i < loopbacks.Length; i++)
            {
                var samples = allSamples[i];
                if (samples == null || samples.Length == 0) continue;
                for (int j = 0; j < samples.Length && j < maxLen; j++)
                {
                    mixed[j] += samples[j];
                }
            }

            return mixed;
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

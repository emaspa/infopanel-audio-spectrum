using InfoPanel.Plugins;
using Serilog;
using System.Diagnostics;

namespace InfoPanel.AudioSpectrum
{
    public class AudioSpectrumPlugin : BasePlugin
    {
        private static readonly ILogger Logger = Log.ForContext<AudioSpectrumPlugin>();

        private readonly List<PluginContainer> _containers = [];
        private readonly SpectrumConfig _config = new();

        private AudioCapture? _capture;
        private SpectrumAnalyzer? _analyzer;
        private SpectrumRenderer? _renderer;
        private SpectrumServer? _server;

        private PluginSensor[] _bandSensors = [];
        private PluginSensor? _peakSensor;
        private PluginSensor? _averageSensor;
        private PluginSensor? _audioLevelSensor;
        private PluginSensor? _dataCountSensor;
        private PluginText? _spectrumImage;
        private PluginText? _deviceText;

        public AudioSpectrumPlugin() : base(
            "audio-spectrum",
            "Audio Spectrum",
            "Real-time audio spectrum visualizer. Add as URL image. Powered by NAudio + SkiaSharp.")
        {
        }

        public override string? ConfigFilePath => SpectrumConfig.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(100); // ~10fps

        public override void Initialize()
        {
            _config.Load();

            // Initialize audio capture
            _capture = new AudioCapture();
            _capture.Start(string.IsNullOrEmpty(_config.AudioDevice) ? null : _config.AudioDevice);

            // Initialize analyzer
            _analyzer = new SpectrumAnalyzer(_config.BandCount, _config.Smoothing, _config.PeakDecay);

            // Initialize renderer
            _renderer = new SpectrumRenderer
            {
                Style = _config.Style,
                Scheme = _config.Scheme,
                CustomColor1 = _config.ParseColor(_config.CustomColor1),
                CustomColor2 = _config.ParseColor(_config.CustomColor2),
                BackgroundColor = _config.ParseColor(_config.BackgroundColor),
                BarSpacing = _config.BarSpacing,
                CornerRadius = _config.CornerRadius,
                ShowPeaks = _config.ShowPeaks,
                ShowReflection = _config.ShowReflection,
                Brightness = _config.Brightness
            };

            // Start HTTP server
            _server = new SpectrumServer(_config.ServerPort, _config.ImageWidth, _config.ImageHeight);
            _server.Start();

            // Render initial blank frame
            var initial = _renderer.Render(new float[_config.BandCount], new float[_config.BandCount],
                _config.ImageWidth, _config.ImageHeight);
            if (initial != null) _server.SetImageData(initial);

            // Create containers and sensors
            var spectrumContainer = new PluginContainer("Spectrum");

            // The URL sensor - user adds this as Http Image (MJPEG stream for real-time)
            _spectrumImage = new PluginText("spectrum-image", "Spectrum Image", _server.ImageUrl ?? "");
            spectrumContainer.Entries.Add(_spectrumImage);

            _peakSensor = new PluginSensor("peak", "Peak Level", 0, "%");
            _averageSensor = new PluginSensor("average", "Average Level", 0, "%");
            _audioLevelSensor = new PluginSensor("audio-level", "Audio Input Level", 0, "");
            _dataCountSensor = new PluginSensor("data-count", "Data Callbacks", 0, "");
            _deviceText = new PluginText("device", "Audio Device",
                string.IsNullOrEmpty(_capture.LastError)
                    ? _capture.DeviceName
                    : $"ERROR: {_capture.LastError}");
            spectrumContainer.Entries.Add(_peakSensor);
            spectrumContainer.Entries.Add(_averageSensor);
            spectrumContainer.Entries.Add(_audioLevelSensor);
            spectrumContainer.Entries.Add(_dataCountSensor);
            spectrumContainer.Entries.Add(_deviceText);

            _containers.Add(spectrumContainer);

            // Per-band sensors
            var bandsContainer = new PluginContainer("Bands");
            _bandSensors = new PluginSensor[_config.BandCount];
            for (int i = 0; i < _config.BandCount; i++)
            {
                string name = GetBandLabel(i, _config.BandCount);
                _bandSensors[i] = new PluginSensor($"band_{i}", name, 0, "%");
                bandsContainer.Entries.Add(_bandSensors[i]);
            }
            _containers.Add(bandsContainer);

            Logger.Information("AudioSpectrum initialized: {Bands} bands, {W}x{H}, style={Style}, url={Url}",
                _config.BandCount, _config.ImageWidth, _config.ImageHeight, _config.Style, _server.ImageUrl);
        }

        public override void Close()
        {
            _capture?.Dispose();
            _renderer?.Dispose();
            _server?.Dispose();
            _capture = null;
            _renderer = null;
            _server = null;
            _analyzer = null;

            Logger.Information("AudioSpectrum closed");
        }

        public override void Load(List<IPluginContainer> containers)
        {
            containers.AddRange(_containers);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        public override void Update()
        {
            if (_capture == null || _analyzer == null || _renderer == null || _server == null) return;

            try
            {
                var samples = _capture.GetLatestSamples();
                _analyzer.ProcessSamples(samples, _capture.SampleRate > 0 ? _capture.SampleRate : 48000);

                var bands = _analyzer.SmoothedBands;
                var peaks = _analyzer.PeakBands;

                // Update sensors
                float max = 0, sum = 0;
                for (int i = 0; i < _bandSensors.Length && i < bands.Length; i++)
                {
                    _bandSensors[i].Value = MathF.Round(bands[i], 1);
                    if (bands[i] > max) max = bands[i];
                    sum += bands[i];
                }

                if (_peakSensor != null) _peakSensor.Value = MathF.Round(max, 1);
                if (_averageSensor != null) _averageSensor.Value = MathF.Round(sum / _bandSensors.Length, 1);
                if (_audioLevelSensor != null) _audioLevelSensor.Value = MathF.Round(_capture.PeakLevel * 100, 2);
                if (_dataCountSensor != null) _dataCountSensor.Value = _capture.DataReceivedCount;

                // Render and push to HTTP server (MJPEG stream)
                var imageData = _renderer.Render(bands, peaks, _config.ImageWidth, _config.ImageHeight);
                if (imageData != null)
                {
                    _server.SetImageData(imageData);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error updating audio spectrum");
            }
        }

        private static string GetBandLabel(int index, int totalBands)
        {
            const float minFreq = 20f;
            const float maxFreq = 20000f;

            float t1 = (float)index / totalBands;
            float t2 = (float)(index + 1) / totalBands;
            float centerFreq = minFreq * MathF.Pow(maxFreq / minFreq, (t1 + t2) / 2);

            if (centerFreq >= 1000)
                return $"{centerFreq / 1000:F1}kHz";
            else
                return $"{centerFreq:F0}Hz";
        }
    }
}

using IniParser;
using IniParser.Model;
using SkiaSharp;
using System.Reflection;

namespace InfoPanel.AudioSpectrum
{
    internal class SpectrumConfig
    {
        private static readonly string ConfigFilePath =
            $"{Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName}.ini";

        private const string SECTION = "AudioSpectrum";

        private IniData? _iniData;
        private bool _isDirty;

        public static string FilePath => ConfigFilePath;

        // Audio device (empty = default, or partial name match e.g. "Speakers" or "Realtek")
        public string AudioDevice { get; private set; } = "";

        // Spectrum settings
        public int BandCount { get; private set; } = 32;
        public int ImageWidth { get; private set; } = 400;
        public int ImageHeight { get; private set; } = 150;
        public SpectrumStyle Style { get; private set; } = SpectrumStyle.Bars;
        public ColorScheme Scheme { get; private set; } = ColorScheme.Neon;
        public string CustomColor1 { get; private set; } = "#00FF80";
        public string CustomColor2 { get; private set; } = "#0080FF";
        public string BackgroundColor { get; private set; } = "#FF000000";
        public float BarSpacing { get; private set; } = 0.3f;
        public float CornerRadius { get; private set; } = 4f;
        public bool ShowPeaks { get; private set; } = true;
        public bool ShowReflection { get; private set; } = false;
        public float Brightness { get; private set; } = 1.0f;
        public float Smoothing { get; private set; } = 0.3f;
        public float PeakDecay { get; private set; } = 0.02f;
        public int ServerPort { get; private set; } = 52400;
        public int RefreshIntervalMs { get; private set; } = 5000;

        public void Load()
        {
            if (File.Exists(ConfigFilePath))
            {
                var parser = new FileIniDataParser();
                _iniData = parser.ReadFile(ConfigFilePath);
            }

            EnsureDefaults();
            ReadValues();
        }

        private void EnsureDefaults()
        {
            _iniData ??= new IniData();

            SetDefault("AudioDevice", "");
            SetDefault("BandCount", "32");
            SetDefault("ImageWidth", "400");
            SetDefault("ImageHeight", "150");
            SetDefault("Style", "Bars");
            SetDefault("ColorScheme", "Neon");
            SetDefault("CustomColor1", "#00FF80");
            SetDefault("CustomColor2", "#0080FF");
            SetDefault("BackgroundColor", "#FF000000");
            SetDefault("BarSpacing", "0.3");
            SetDefault("CornerRadius", "4");
            SetDefault("ShowPeaks", "true");
            SetDefault("ShowReflection", "false");
            SetDefault("Brightness", "1.0");
            SetDefault("Smoothing", "0.3");
            SetDefault("PeakDecay", "0.02");
            SetDefault("ServerPort", "52400");
            SetDefault("RefreshIntervalMs", "5000");

            // Write available audio devices as comments
            try
            {
                var devices = WasapiLoopback.GetDeviceNames();
                if (devices.Count > 0)
                {
                    var section = _iniData![SECTION];
                    var keyData = section.GetKeyData("AudioDevice");
                    if (keyData != null)
                    {
                        keyData.Comments.Clear();
                        keyData.Comments.Add("Leave empty for default device, or set to a device name (partial match).");
                        keyData.Comments.Add("Available devices:");
                        foreach (var d in devices)
                            keyData.Comments.Add($"  {d}");
                    }
                    _isDirty = true;
                }
            }
            catch { }

            if (_isDirty)
            {
                var parser = new FileIniDataParser();
                parser.WriteFile(ConfigFilePath, _iniData!);
                _isDirty = false;
            }
        }

        private void SetDefault(string key, string value)
        {
            if (_iniData != null && !_iniData[SECTION].ContainsKey(key))
            {
                _iniData[SECTION][key] = value;
                _isDirty = true;
            }
        }

        private void ReadValues()
        {
            if (_iniData == null) return;

            AudioDevice = GetString("AudioDevice", "");
            BandCount = GetInt("BandCount", 32);
            ImageWidth = GetInt("ImageWidth", 400);
            ImageHeight = GetInt("ImageHeight", 150);
            Style = GetEnum("Style", SpectrumStyle.Bars);
            Scheme = GetEnum("ColorScheme", ColorScheme.Neon);
            CustomColor1 = GetString("CustomColor1", "#00FF80");
            CustomColor2 = GetString("CustomColor2", "#0080FF");
            BackgroundColor = GetString("BackgroundColor", "Transparent");
            BarSpacing = GetFloat("BarSpacing", 0.3f);
            CornerRadius = GetFloat("CornerRadius", 4f);
            ShowPeaks = GetBool("ShowPeaks", true);
            ShowReflection = GetBool("ShowReflection", false);
            Brightness = GetFloat("Brightness", 1.0f);
            Smoothing = GetFloat("Smoothing", 0.3f);
            PeakDecay = GetFloat("PeakDecay", 0.02f);
            ServerPort = GetInt("ServerPort", 52400);
            RefreshIntervalMs = GetInt("RefreshIntervalMs", 5000);

            // Clamp values
            BandCount = Math.Clamp(BandCount, 8, 128);
            ImageWidth = Math.Clamp(ImageWidth, 100, 3840);
            ImageHeight = Math.Clamp(ImageHeight, 50, 2160);
            BarSpacing = Math.Clamp(BarSpacing, 0f, 0.8f);
            CornerRadius = Math.Clamp(CornerRadius, 0f, 20f);
            Brightness = Math.Clamp(Brightness, 0.1f, 2.0f);
            Smoothing = Math.Clamp(Smoothing, 0.05f, 0.95f);
            PeakDecay = Math.Clamp(PeakDecay, 0.005f, 0.1f);
            ServerPort = Math.Clamp(ServerPort, 1024, 65535);
            RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, 1000, 60000);
        }

        private int GetInt(string key, int defaultValue)
        {
            var val = _iniData?[SECTION][key];
            return int.TryParse(val, out int result) ? result : defaultValue;
        }

        private float GetFloat(string key, float defaultValue)
        {
            var val = _iniData?[SECTION][key];
            return float.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out float result) ? result : defaultValue;
        }

        private bool GetBool(string key, bool defaultValue)
        {
            var val = _iniData?[SECTION][key];
            return bool.TryParse(val, out bool result) ? result : defaultValue;
        }

        private string GetString(string key, string defaultValue)
        {
            var val = _iniData?[SECTION][key];
            return string.IsNullOrEmpty(val) ? defaultValue : val;
        }

        private T GetEnum<T>(string key, T defaultValue) where T : struct, Enum
        {
            var val = _iniData?[SECTION][key];
            return Enum.TryParse(val, true, out T result) ? result : defaultValue;
        }

        public SKColor ParseColor(string colorStr)
        {
            if (string.Equals(colorStr, "Transparent", StringComparison.OrdinalIgnoreCase))
                return SKColors.Transparent;

            if (SKColor.TryParse(colorStr, out var color))
                return color;

            return SKColors.Transparent;
        }
    }
}

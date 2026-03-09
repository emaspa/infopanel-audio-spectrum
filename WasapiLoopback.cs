using System.Runtime.InteropServices;

namespace InfoPanel.AudioSpectrum
{
    /// <summary>
    /// Pure P/Invoke WASAPI loopback capture using raw COM vtable calls.
    /// No [ComImport] interfaces - works in any AssemblyLoadContext.
    /// </summary>
    internal sealed class WasapiLoopback : IDisposable
    {
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint CLSCTX_ALL = 0x17;
        private const int DEVICE_STATE_ACTIVE = 0x1;

        private IntPtr _audioClient;
        private IntPtr _captureClient;
        private Thread? _captureThread;
        private volatile bool _capturing;
        private bool _disposed;

        private readonly object _lock = new();
        private float[] _buffer = [];
        private float _peakLevel;
        private long _dataCount;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public float PeakLevel => _peakLevel;
        public long DataCount => _dataCount;
        public string DeviceName { get; private set; } = "";
        public string? Error { get; private set; }

        /// <summary>
        /// Returns list of active render device names (for loopback capture).
        /// </summary>
        public static List<string> GetDeviceNames()
        {
            var names = new List<string>();
            try
            {
                CoInitializeEx(IntPtr.Zero, 0x0);
                var clsid = CLSID_MMDeviceEnumerator;
                var iid = IID_IMMDeviceEnumerator;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var enumerator);
                if (hr < 0) return names;

                try
                {
                    var vtbl = GetVtbl(enumerator);
                    // IMMDeviceEnumerator::EnumAudioEndpoints (vtable slot 3)
                    var enumEndpoints = Marshal.GetDelegateForFunctionPointer<EnumAudioEndpointsDelegate>(
                        Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
                    hr = enumEndpoints(enumerator, 0 /*eRender*/, DEVICE_STATE_ACTIVE, out var collection);
                    if (hr < 0) return names;

                    try
                    {
                        var colVtbl = GetVtbl(collection);
                        // IMMDeviceCollection::GetCount (vtable slot 3)
                        var getCount = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(
                            Marshal.ReadIntPtr(colVtbl, 3 * IntPtr.Size));
                        hr = getCount(collection, out var count);
                        if (hr < 0) return names;

                        // IMMDeviceCollection::Item (vtable slot 4)
                        var item = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(
                            Marshal.ReadIntPtr(colVtbl, 4 * IntPtr.Size));

                        for (uint i = 0; i < count; i++)
                        {
                            hr = item(collection, i, out var device);
                            if (hr < 0) continue;
                            try
                            {
                                names.Add(GetDeviceFriendlyName(device));
                            }
                            finally
                            {
                                Marshal.Release(device);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(collection);
                    }
                }
                finally
                {
                    Marshal.Release(enumerator);
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// Start capturing. If deviceName is null/empty, uses default render device.
        /// Otherwise finds the device whose friendly name contains the given string.
        /// </summary>
        public void Start(string? deviceName = null)
        {
            try
            {
                CoInitializeEx(IntPtr.Zero, 0x0 /*COINIT_MULTITHREADED*/);

                var clsid = CLSID_MMDeviceEnumerator;
                var iid = IID_IMMDeviceEnumerator;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var enumerator);
                ThrowIfFailed(hr, "CoCreateInstance MMDeviceEnumerator");

                try
                {
                    IntPtr device;

                    if (string.IsNullOrEmpty(deviceName))
                    {
                        // Use default render endpoint
                        var vtbl = GetVtbl(enumerator);
                        var getDefaultEndpoint = Marshal.GetDelegateForFunctionPointer<GetDefaultAudioEndpointDelegate>(
                            Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size));
                        hr = getDefaultEndpoint(enumerator, 0 /*eRender*/, 1 /*eMultimedia*/, out device);
                        ThrowIfFailed(hr, "GetDefaultAudioEndpoint");
                    }
                    else
                    {
                        // Find device by name
                        device = FindDeviceByName(enumerator, deviceName);
                        if (device == IntPtr.Zero)
                            throw new Exception($"Audio device not found: '{deviceName}'");
                    }

                    try
                    {
                        DeviceName = GetDeviceFriendlyName(device);
                        InitializeCapture(device);
                        AudioCapture.WriteErrorLog($"OK: {DeviceName}, {SampleRate}Hz, {Channels}ch");
                    }
                    finally
                    {
                        Marshal.Release(device);
                    }
                }
                finally
                {
                    Marshal.Release(enumerator);
                }
            }
            catch (Exception ex)
            {
                Error = $"{ex.GetType().Name}: {ex.Message}";
                AudioCapture.WriteErrorLog($"WasapiLoopback.Start: {ex}");
            }
        }

        private IntPtr FindDeviceByName(IntPtr enumerator, string searchName)
        {
            var vtbl = GetVtbl(enumerator);
            var enumEndpoints = Marshal.GetDelegateForFunctionPointer<EnumAudioEndpointsDelegate>(
                Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
            int hr = enumEndpoints(enumerator, 0 /*eRender*/, DEVICE_STATE_ACTIVE, out var collection);
            if (hr < 0) return IntPtr.Zero;

            try
            {
                var colVtbl = GetVtbl(collection);
                var getCount = Marshal.GetDelegateForFunctionPointer<GetCountDelegate>(
                    Marshal.ReadIntPtr(colVtbl, 3 * IntPtr.Size));
                hr = getCount(collection, out var count);
                if (hr < 0) return IntPtr.Zero;

                var item = Marshal.GetDelegateForFunctionPointer<ItemDelegate>(
                    Marshal.ReadIntPtr(colVtbl, 4 * IntPtr.Size));

                for (uint i = 0; i < count; i++)
                {
                    hr = item(collection, i, out var device);
                    if (hr < 0) continue;

                    var name = GetDeviceFriendlyName(device);
                    if (name.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found it - don't release, caller will release
                        return device;
                    }
                    Marshal.Release(device);
                }
            }
            finally
            {
                Marshal.Release(collection);
            }

            return IntPtr.Zero;
        }

        private void InitializeCapture(IntPtr device)
        {
            var deviceVtbl = GetVtbl(device);
            var activate = Marshal.GetDelegateForFunctionPointer<ActivateDelegate>(
                Marshal.ReadIntPtr(deviceVtbl, 3 * IntPtr.Size));
            var iidAudioClient = IID_IAudioClient;
            int hr = activate(device, ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out _audioClient);
            ThrowIfFailed(hr, "Activate IAudioClient");

            var acVtbl = GetVtbl(_audioClient);

            // GetMixFormat (vtable slot 8)
            var getMixFormat = Marshal.GetDelegateForFunctionPointer<GetMixFormatDelegate>(
                Marshal.ReadIntPtr(acVtbl, 8 * IntPtr.Size));
            hr = getMixFormat(_audioClient, out var formatPtr);
            ThrowIfFailed(hr, "GetMixFormat");

            var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            SampleRate = format.nSamplesPerSec;
            Channels = format.nChannels;

            // Initialize (vtable slot 3)
            var initialize = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(
                Marshal.ReadIntPtr(acVtbl, 3 * IntPtr.Size));
            hr = initialize(_audioClient,
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK,
                10_000_000L, 0, formatPtr, IntPtr.Zero);
            ThrowIfFailed(hr, "Initialize");

            CoTaskMemFree(formatPtr);

            // GetService (vtable slot 14)
            var getService = Marshal.GetDelegateForFunctionPointer<GetServiceDelegate>(
                Marshal.ReadIntPtr(acVtbl, 14 * IntPtr.Size));
            var iidCapture = IID_IAudioCaptureClient;
            hr = getService(_audioClient, ref iidCapture, out _captureClient);
            ThrowIfFailed(hr, "GetService IAudioCaptureClient");

            // Start (vtable slot 10)
            var start = Marshal.GetDelegateForFunctionPointer<NoArgDelegate>(
                Marshal.ReadIntPtr(acVtbl, 10 * IntPtr.Size));
            hr = start(_audioClient);
            ThrowIfFailed(hr, "Start");

            _capturing = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "AudioSpectrum-Capture"
            };
            _captureThread.Start();
        }

        private static string GetDeviceFriendlyName(IntPtr device)
        {
            try
            {
                var deviceVtbl = GetVtbl(device);
                // IMMDevice::OpenPropertyStore (vtable slot 4)
                var openPropStore = Marshal.GetDelegateForFunctionPointer<OpenPropertyStoreDelegate>(
                    Marshal.ReadIntPtr(deviceVtbl, 4 * IntPtr.Size));
                int hr = openPropStore(device, 0 /*STGM_READ*/, out var propStore);
                if (hr != 0) return "Unknown";

                try
                {
                    var psVtbl = GetVtbl(propStore);
                    // IPropertyStore::GetValue (vtable slot 5)
                    var getValue = Marshal.GetDelegateForFunctionPointer<GetValueDelegate>(
                        Marshal.ReadIntPtr(psVtbl, 5 * IntPtr.Size));

                    var nameKey = new PROPERTYKEY
                    {
                        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                        pid = 14 // PKEY_Device_FriendlyName
                    };

                    var propVariant = Marshal.AllocCoTaskMem(24);
                    try
                    {
                        for (int i = 0; i < 24; i++)
                            Marshal.WriteByte(propVariant, i, 0);

                        hr = getValue(propStore, ref nameKey, propVariant);
                        if (hr != 0) return "Unknown";

                        var vt = Marshal.ReadInt16(propVariant);
                        if (vt == 31) // VT_LPWSTR
                        {
                            var strPtr = Marshal.ReadIntPtr(propVariant, 8);
                            var name = Marshal.PtrToStringUni(strPtr);
                            PropVariantClear(propVariant);
                            return name ?? "Unknown";
                        }

                        PropVariantClear(propVariant);
                        return "Unknown";
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(propVariant);
                    }
                }
                finally
                {
                    Marshal.Release(propStore);
                }
            }
            catch
            {
                return "Unknown";
            }
        }

        private void CaptureLoop()
        {
            var ccVtbl = GetVtbl(_captureClient);
            var getNextPacketSize = Marshal.GetDelegateForFunctionPointer<GetNextPacketSizeDelegate>(
                Marshal.ReadIntPtr(ccVtbl, 5 * IntPtr.Size));
            var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(
                Marshal.ReadIntPtr(ccVtbl, 3 * IntPtr.Size));
            var releaseBuffer = Marshal.GetDelegateForFunctionPointer<ReleaseBufferDelegate>(
                Marshal.ReadIntPtr(ccVtbl, 4 * IntPtr.Size));

            while (_capturing)
            {
                try
                {
                    Thread.Sleep(10);

                    int hr = getNextPacketSize(_captureClient, out var packetSize);
                    if (hr != 0) continue;

                    while (packetSize > 0)
                    {
                        hr = getBuffer(_captureClient, out var dataPtr, out var numFrames, out _, out _, out _);
                        if (hr != 0) break;

                        if (numFrames > 0 && dataPtr != IntPtr.Zero)
                        {
                            var mono = new float[numFrames];
                            float peak = 0;
                            int channels = Channels;

                            unsafe
                            {
                                float* src = (float*)dataPtr;
                                for (int i = 0; i < numFrames; i++)
                                {
                                    float sum = 0;
                                    for (int ch = 0; ch < channels; ch++)
                                        sum += src[i * channels + ch];
                                    mono[i] = sum / channels;
                                    float abs = MathF.Abs(mono[i]);
                                    if (abs > peak) peak = abs;
                                }
                            }

                            _peakLevel = peak;
                            Interlocked.Increment(ref _dataCount);

                            lock (_lock)
                            {
                                _buffer = mono;
                            }
                        }

                        releaseBuffer(_captureClient, numFrames);
                        hr = getNextPacketSize(_captureClient, out packetSize);
                        if (hr != 0) break;
                    }
                }
                catch { }
            }
        }

        public float[] GetLatestSamples()
        {
            lock (_lock)
            {
                if (_buffer.Length == 0) return [];
                var copy = new float[_buffer.Length];
                Array.Copy(_buffer, copy, _buffer.Length);
                return copy;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _capturing = false;

            _captureThread?.Join(1000);

            if (_audioClient != IntPtr.Zero)
            {
                try
                {
                    var acVtbl = GetVtbl(_audioClient);
                    var stop = Marshal.GetDelegateForFunctionPointer<NoArgDelegate>(
                        Marshal.ReadIntPtr(acVtbl, 11 * IntPtr.Size));
                    stop(_audioClient);
                }
                catch { }
                Marshal.Release(_audioClient);
            }
            if (_captureClient != IntPtr.Zero)
                Marshal.Release(_captureClient);
        }

        private static IntPtr GetVtbl(IntPtr comObj) => Marshal.ReadIntPtr(comObj);

        private static void ThrowIfFailed(int hr, string context)
        {
            if (hr < 0) throw new COMException($"{context} failed", hr);
        }

        // P/Invoke
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(IntPtr pvar);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        // COM vtable delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAudioEndpointsDelegate(IntPtr self, int dataFlow, int stateMask, out IntPtr collection);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, int dataFlow, int role, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCountDelegate(IntPtr self, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ItemDelegate(IntPtr self, uint nDevice, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateDelegate(IntPtr self, ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OpenPropertyStoreDelegate(IntPtr self, int stgmAccess, out IntPtr propStore);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetValueDelegate(IntPtr self, ref PROPERTYKEY key, IntPtr pv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(IntPtr self, int shareMode, int streamFlags,
            long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMixFormatDelegate(IntPtr self, out IntPtr ppDeviceFormat);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NoArgDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetServiceDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferDelegate(IntPtr self, out IntPtr ppData, out uint pNumFramesToRead,
            out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseBufferDelegate(IntPtr self, uint numFramesRead);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNextPacketSizeDelegate(IntPtr self, out uint pNumFramesInNextPacket);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }
    }
}

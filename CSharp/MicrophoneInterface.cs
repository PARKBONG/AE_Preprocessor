using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using OpenCvSharp;
using System.Collections.Concurrent;

namespace NS_SensorInterface
{
    public struct SpectrogramData
    {
        public float[] Magnitudes;
        public float Intensity; // Added for audio intensity
        public DateTime Timestamp;
    }

    public class MicrophoneInterfaceConfig
    {
        // Hardware
        public int DeviceId { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }

        // Analysis
        public int FftSize { get; set; }
        public int HopSize { get; set; }
        public string WindowType { get; set; } = string.Empty;

        // Operations
        public bool EnableDataCollection { get; set; }
        public bool EnableMonitoring { get; set; }
        public bool EnableInference { get; set; }

        // Storage
        public string BaseLogDirectory { get; set; } = string.Empty;
        public int FileRotationSeconds { get; set; }

        // Inference
        public int StripWidth { get; set; }
        public bool SaveImages { get; set; }

        public static MicrophoneInterfaceConfig Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"[MicrophoneInterface] Configuration file not found at: {filePath}");
            }

            try
            {
                var serializer = new XmlSerializer(typeof(MicrophoneInterfaceConfig));
                using (var reader = new StreamReader(filePath))
                {
                    var config = (MicrophoneInterfaceConfig)serializer.Deserialize(reader)!;
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MicrophoneInterface] Failed to load config: {ex.Message}");
                throw; // Rethrow to ensure the caller knows initialization failed
            }
        }

        public void Save(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(MicrophoneInterfaceConfig));
                using (var writer = new StreamWriter(filePath))
                {
                    serializer.Serialize(writer, this);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MicrophoneInterface] Failed to save config: {ex.Message}");
            }
        }
    }

    public class MicrophoneInterface : IDisposable
    {
        private readonly MicrophoneInterfaceConfig _config;
        public MicrophoneInterfaceConfig Config => _config;

        public event Action<SpectrogramData>? OnSpectrogramUpdate;

        private WasapiCapture? _capture;
        private Task? _processingTask;
        private CancellationTokenSource? _cts;

        private BlockingCollection<float[]>? _recordingQueue;
        private Task? _recordingTask;

        private readonly float[] _processingFrame;
        private readonly float[] _magnitudeFrame;
        private readonly float[] _smoothedMagnitudeFrame; // For temporal smoothing

        private unsafe float* _nativeBuffer;
        private readonly int _nativeBufferCapacity;
        private int _bufferWritePos;
        private long _totalSamplesWritten;

        private readonly int _fftSize;
        private readonly float[] _window;
        private readonly Complex[] _fftBuffer;

        private WaveFileWriter? _wavWriter;
        private string _baseDirectory = string.Empty;
        private int _fileIndex = 0;
        private int _rotationSeconds;
        private DateTime _fileStartTime;
        private DateTime _sessionStartTime;
        private readonly object _recorderLock = new object();

        public TimeSpan RecordingDuration => _cts != null ? DateTime.Now - _sessionStartTime : TimeSpan.Zero;
        public DateTime SessionStartTime => _sessionStartTime;
        public string SessionPath { get; private set; } = string.Empty;
        public string SessionTimestamp { get; private set; } = string.Empty;

        private float _lastIntensity = 0;

        public unsafe MicrophoneInterface(string configPath = "config/Sensor_Interface/MicrophoneInterfaceConfig.xml", string? sessionTimestamp = null)
        {
            string fullPath = Path.Combine(AppContext.BaseDirectory, configPath);
            _config = MicrophoneInterfaceConfig.Load(fullPath);

            Console.WriteLine($"[MicrophoneInterface] Config loaded from {fullPath}");
            Console.WriteLine($"[MicrophoneInterface] Hardware: SampleRate={_config.SampleRate}Hz, BitsPerSample={_config.BitsPerSample}, Channels={_config.Channels}");
            Console.WriteLine($"[MicrophoneInterface] Analysis: FftSize={_config.FftSize}, HopSize={_config.HopSize}, Window={_config.WindowType}");
            Console.WriteLine($"[MicrophoneInterface] Operations: Collection={_config.EnableDataCollection}, Monitoring={_config.EnableMonitoring}, Inference={_config.EnableInference}");

            try
            {
                _capture = new WasapiCapture();
                _capture.WaveFormat = new WaveFormat(_config.SampleRate, _config.BitsPerSample, _config.Channels);
                _capture.DataAvailable += OnDataAvailable;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MicrophoneInterface] Warning: Could not initialize audio capture device: {ex.Message}");
                _capture = null;
            }

            _nativeBufferCapacity = _config.SampleRate * 5;
            _nativeBuffer = (float*)Marshal.AllocHGlobal(_nativeBufferCapacity * sizeof(float));
            for (int i = 0; i < _nativeBufferCapacity; i++) _nativeBuffer[i] = 0;

            _fftSize = _config.FftSize;
            _window = new float[_fftSize];
            _fftBuffer = new Complex[_fftSize];

            var hannWindow = MathNet.Numerics.Window.Hann(_fftSize);
            for (int i = 0; i < _fftSize; i++) _window[i] = (float)hannWindow[i];

            _processingFrame = new float[_fftSize];
            _magnitudeFrame = new float[_fftSize / 2];
            _smoothedMagnitudeFrame = new float[_fftSize / 2];
            for (int i = 0; i < _magnitudeFrame.Length; i++) _smoothedMagnitudeFrame[i] = -100f; // Initial floor

            if (_config.EnableDataCollection)
            {
                // Sync logic: Use provided timestamp or create new one
                SessionTimestamp = sessionTimestamp ?? DateTime.Now.ToString("yyMMdd_HHmmss");
                SessionPath = Path.Combine(_config.BaseLogDirectory, SessionTimestamp);

                _baseDirectory = Path.Combine(SessionPath, "Audio");
                _rotationSeconds = _config.FileRotationSeconds;
                _fileIndex = 0;
                _sessionStartTime = DateTime.Now;

                Console.WriteLine($"[MicrophoneInterface] Data collection enabled. Path: {_baseDirectory}");
            }
        }

        public void Start()
        {
            if (_processingTask != null) return;

            _cts = new CancellationTokenSource();

            if (_config.EnableDataCollection)
            {
                _sessionStartTime = DateTime.Now;
                _recordingQueue = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>());
                _recordingTask = Task.Run(() => RecordingLoop(_cts.Token), _cts.Token);
            }

            _capture?.StartRecording();

            _processingTask = Task.Run(() => ProcessingLoop(_cts.Token), _cts.Token);
            Console.WriteLine($"[MicrophoneInterface] Started. Mode: Collection={_config.EnableDataCollection}, Monitoring={_config.EnableMonitoring}");
        }

        public void Stop()
        {
            Dispose();
        }

        private unsafe void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int bytesPerSample = _config.BitsPerSample / 8;
            int sampleCount = e.BytesRecorded / bytesPerSample;
            float[] floatSamples = new float[sampleCount];
            double sumSq = 0;

            fixed (byte* pBuffer = e.Buffer)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    float val = 0;
                    if (_config.BitsPerSample == 16)
                    {
                        val = (*(short*)(pBuffer + i * 2)) / 32768f;
                    }
                    else if (_config.BitsPerSample == 24)
                    {
                        // 24-bit handling (3 bytes)
                        int sample = (pBuffer[i * 3 + 0] << 0) | (pBuffer[i * 3 + 1] << 8) | (pBuffer[i * 3 + 2] << 16);
                        if ((sample & 0x800000) != 0) sample |= unchecked((int)0xff000000); // Sign extend
                        val = sample / 8388608f;
                    }
                    else if (_config.BitsPerSample == 32)
                    {
                        val = *(float*)(pBuffer + i * 4);
                    }

                    floatSamples[i] = val;
                    sumSq += val * val;
                }
            }

            float rawIntensity = (float)Math.Sqrt(sumSq / sampleCount);
            _lastIntensity = rawIntensity;

            WriteToNativeBuffer(floatSamples);

            if (_config.EnableDataCollection)
            {
                _recordingQueue?.Add(floatSamples);
            }
        }

        private async Task ProcessingLoop(CancellationToken token)
        {
            long lastProcessedSample = 0;

            while (!token.IsCancellationRequested)
            {
                if (!_config.EnableMonitoring)
                {
                    lastProcessedSample = _totalSamplesWritten;
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                long currentTotal = _totalSamplesWritten;

                if (currentTotal - lastProcessedSample >= _config.HopSize)
                {
                    ReadLatestFromNativeBuffer(_processingFrame, _fftSize);

                    // Calculate raw intensity (RMS) for this specific frame
                    double sumSq = 0;
                    for (int i = 0; i < _fftSize; i++)
                    {
                        sumSq += _processingFrame[i] * _processingFrame[i];
                    }
                    float frameIntensity = (float)Math.Sqrt(sumSq / _fftSize);

                    ProcessFrame(_processingFrame, _magnitudeFrame);

                    // Apply temporal smoothing (EMA) - Spectrogram only
                    float alpha = 0.5f;
                    for (int i = 0; i < _magnitudeFrame.Length; i++)
                    {
                        _smoothedMagnitudeFrame[i] = alpha * _magnitudeFrame[i] + (1 - alpha) * _smoothedMagnitudeFrame[i];
                    }

                    float[] resultCopy = new float[_smoothedMagnitudeFrame.Length];
                    Array.Copy(_smoothedMagnitudeFrame, resultCopy, _smoothedMagnitudeFrame.Length);

                    OnSpectrogramUpdate?.Invoke(new SpectrogramData
                    {
                        Magnitudes = resultCopy,
                        Intensity = frameIntensity, // Per-frame raw intensity
                        Timestamp = DateTime.Now
                    });

                    lastProcessedSample += _config.HopSize;
                }
                else
                {
                    await Task.Delay(4, token).ConfigureAwait(false);
                }
            }
        }

        private void ProcessFrame(ReadOnlySpan<float> samples, Span<float> outputMagnitudes)
        {
            for (int i = 0; i < _fftSize; i++)
            {
                _fftBuffer[i] = new Complex(samples[i] * _window[i], 0);
            }

            Fourier.Forward(_fftBuffer, FourierOptions.NoScaling);

            float invFftSize = 2.0f / _fftSize;
            for (int i = 0; i < _fftSize / 2; i++)
            {
                double mag = _fftBuffer[i].Magnitude * invFftSize;
                float db = (float)(20.0 * Math.Log10(Math.Max(mag, 1e-10)));
                outputMagnitudes[i] = db;
            }
        }

        private unsafe void WriteToNativeBuffer(ReadOnlySpan<float> samples)
        {
            fixed (float* srcPtr = samples)
            {
                int remaining = samples.Length;
                int srcOffset = 0;

                while (remaining > 0)
                {
                    int spaceToEnd = _nativeBufferCapacity - _bufferWritePos;
                    int chunk = Math.Min(remaining, spaceToEnd);

                    Buffer.MemoryCopy(srcPtr + srcOffset, _nativeBuffer + _bufferWritePos, (long)spaceToEnd * sizeof(float), (long)chunk * sizeof(float));

                    _bufferWritePos = (_bufferWritePos + chunk) % _nativeBufferCapacity;
                    srcOffset += chunk;
                    remaining -= chunk;
                }
            }
            _totalSamplesWritten += samples.Length;
        }

        private unsafe void ReadLatestFromNativeBuffer(Span<float> destination, int count)
        {
            int startPos = (_bufferWritePos - count + _nativeBufferCapacity) % _nativeBufferCapacity;

            if (startPos + count <= _nativeBufferCapacity)
            {
                new Span<float>(_nativeBuffer + startPos, count).CopyTo(destination);
            }
            else
            {
                int firstPart = _nativeBufferCapacity - startPos;
                int secondPart = count - firstPart;
                new Span<float>(_nativeBuffer + startPos, firstPart).CopyTo(destination.Slice(0, firstPart));
                new Span<float>(_nativeBuffer, secondPart).CopyTo(destination.Slice(firstPart));
            }
        }

        private void RecordingLoop(CancellationToken token)
        {
            Console.WriteLine("[MicrophoneInterface] Recording loop started.");

            try
            {
                while (!token.IsCancellationRequested || (_recordingQueue != null && !_recordingQueue.IsCompleted))
                {
                    if (_recordingQueue != null && _recordingQueue.TryTake(out float[]? samples, 100))
                    {
                        WriteSamplesToFile(samples);
                    }
                    else if (token.IsCancellationRequested && (_recordingQueue == null || _recordingQueue.IsCompleted))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[MicrophoneInterface] RecordingLoop error: {ex.Message}");
            }
            finally
            {
                lock (_recorderLock)
                {
                    _wavWriter?.Dispose();
                    _wavWriter = null;
                }
                Console.WriteLine("[MicrophoneInterface] Recording loop terminated and flushed.");
            }
        }

        private void WriteSamplesToFile(float[] samples)
        {
            lock (_recorderLock)
            {
                if (_wavWriter == null || (DateTime.Now - _fileStartTime).TotalSeconds >= _rotationSeconds)
                {
                    _wavWriter?.Dispose();

                    try
                    {
                        if (!Directory.Exists(_baseDirectory)) Directory.CreateDirectory(_baseDirectory);
                        string fileName = $"{_fileIndex++}.wav";
                        string path = Path.Combine(_baseDirectory, fileName);

                        // Use the format from capture or config to preserve quality
                        var format = _capture?.WaveFormat ?? new WaveFormat(_config.SampleRate, _config.BitsPerSample, _config.Channels);
                        _wavWriter = new WaveFileWriter(path, format);
                        _fileStartTime = DateTime.Now;
                        Console.WriteLine($"[MicrophoneInterface] New file started: {path}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MicrophoneInterface] Failed to create WAV file: {ex.Message}");
                        return;
                    }
                }

                _wavWriter.WriteSamples(samples, 0, samples.Length);
            }
        }

        private bool _disposed = false;
        public unsafe void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _capture?.StopRecording();
            _cts?.Cancel();

            try
            {
                _processingTask?.Wait(2000);
                _recordingQueue?.CompleteAdding();
                _recordingTask?.Wait(5000); // Give more time for disk I/O flush
            }
            catch { }

            _capture?.Dispose();
            _cts?.Dispose();
            _recordingQueue?.Dispose();

            if (_nativeBuffer != null)
            {
                Marshal.FreeHGlobal((IntPtr)_nativeBuffer);
                _nativeBuffer = null;
            }

            lock (_recorderLock)
            {
                _wavWriter?.Dispose();
                _wavWriter = null;
            }
        }
    }
}

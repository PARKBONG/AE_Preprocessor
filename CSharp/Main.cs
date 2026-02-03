using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using NAudio.Wave;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using OpenCvSharp;
using System.Xml.Serialization;

namespace AE_Spectrogram
{
    // Reusing the config class structure
    public class MicrophoneInterfaceConfig
    {
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public int FftSize { get; set; }
        public int HopSize { get; set; }
        public int StripWidth { get; set; }
        public string BaseLogDirectory { get; set; } = "";

        public static MicrophoneInterfaceConfig Load(string filePath)
        {
            var serializer = new XmlSerializer(typeof(MicrophoneInterfaceConfig));
            using (var reader = new StreamReader(filePath))
            {
                return (MicrophoneInterfaceConfig)serializer.Deserialize(reader)!;
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            string baseDir = AppContext.BaseDirectory;
            string configPath = Path.Combine(baseDir, "config", "Sensor_Interface", "MicrophoneInterfaceConfig.xml");
            string inputRootDir = Path.GetFullPath(Path.Combine(baseDir, "../../../../DB_kunkuk"));
            string outputRootDir = Path.GetFullPath(Path.Combine(baseDir, "../../../../CS_Output/spectrogram"));

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Config not found: {configPath}");
                return;
            }

            var config = MicrophoneInterfaceConfig.Load(configPath);
            Console.WriteLine("[Processor] Config Loaded.");
            Console.WriteLine($"[Processor] SR: {config.SampleRate}, FFT: {config.FftSize}, Hop: {config.HopSize}, Strip: {config.StripWidth}");

            if (!Directory.Exists(inputRootDir))
            {
                Console.WriteLine($"Input root directory not found: {inputRootDir}");
                return;
            }

            var subDirs = Directory.GetDirectories(inputRootDir);
            foreach (var subDir in subDirs)
            {
                string dirName = Path.GetFileName(subDir);
                Console.WriteLine($"\n[Directory] {dirName} Processing...");

                var wavFiles = Directory.GetFiles(subDir, "*.wav", SearchOption.AllDirectories)
                    .OrderBy(f => {
                        string filename = Path.GetFileNameWithoutExtension(f);
                        return int.TryParse(filename, out int n) ? n : int.MaxValue;
                    })
                    .ToList();

                if (wavFiles.Count == 0)
                {
                    Console.WriteLine($"[Warning] No .wav files found in {subDir}");
                    continue;
                }

                double totalDuration = 0;
                List<float> allSamples = new List<float>();

                foreach (var wavFile in wavFiles)
                {
                    try
                    {
                        using (var reader = new AudioFileReader(wavFile))
                        {
                            totalDuration += reader.TotalTime.TotalSeconds;
                            float[] buffer = new float[reader.Length / (reader.WaveFormat.BitsPerSample / 8)];
                            int read = reader.Read(buffer, 0, buffer.Length);
                            allSamples.AddRange(buffer.Take(read));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to read {wavFile}: {ex.Message}");
                    }
                }

                if (allSamples.Count == 0)
                {
                    Console.WriteLine($"[Warning] No valid audio data in {subDir}");
                    continue;
                }

                Console.WriteLine($"[Summary] Total Samples: {allSamples.Count}, Total Duration: {totalDuration:F2} seconds");

                // Process Spectrogram
                string outputDir = Path.Combine(outputRootDir, dirName);
                if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

                ProcessAndSaveSpectrogram(allSamples.ToArray(), config, outputDir);
            }

            Console.WriteLine("\n[Done] All directories processed.");
        }

        static void ProcessAndSaveSpectrogram(float[] samples, MicrophoneInterfaceConfig config, string outputDir)
        {
            int fftSize = config.FftSize;
            int hopSize = config.HopSize;
            int stripWidth = config.StripWidth;
            int freqBins = fftSize / 2;

            var window = MathNet.Numerics.Window.Hann(fftSize).Select(v => (float)v).ToArray();
            var fftBuffer = new Complex[fftSize];
            
            List<float[]> frames = new List<float[]>();

            for (int i = 0; i + fftSize <= samples.Length; i += hopSize)
            {
                for (int j = 0; j < fftSize; j++)
                {
                    fftBuffer[j] = new Complex(samples[i + j] * window[j], 0);
                }

                Fourier.Forward(fftBuffer, FourierOptions.NoScaling);

                float[] magnitudes = new float[freqBins];
                float invFftSize = 2.0f / fftSize; 

                // dB gating: Noise Reduction
                for (int m = 0; m < freqBins; m++)
                {
                    double mag = fftBuffer[m].Magnitude * invFftSize;
                    float db = (float)(20.0 * Math.Log10(Math.Max(mag, 1e-10)));
                    
                    // Normalize dB (-100 to 0 -> 0 to 255)
                    float normalized = (db + 100f) * 2.55f;
                    magnitudes[m] = Math.Clamp(normalized, 0, 255);
                }
                frames.Add(magnitudes);
            }

            // Save Strips
            int stripCount = frames.Count / stripWidth;
            for (int s = 0; s < stripCount; s++)
            {
                using (Mat mat = new Mat(freqBins, stripWidth, MatType.CV_8UC1))
                {
                    var indexer = mat.GetGenericIndexer<byte>();
                    for (int x = 0; x < stripWidth; x++)
                    {
                        var frame = frames[s * stripWidth + x];
                        for (int y = 0; y < freqBins; y++)
                        {
                            // OpenCV Y-axis: 0 is top. 
                            // Standard Spectrogram: Higher frequency at top.
                            // So bin freqBins-1 is top, bin 0 is bottom.
                            indexer[freqBins - 1 - y, x] = (byte)frame[y];
                        }
                    }
                    string fileName = Path.Combine(outputDir, $"strip_{s:D4}.png");
                    Cv2.ImWrite(fileName, mat);
                }
            }

            Console.WriteLine($"[Output] Generated {stripCount} strips in {outputDir}");
        }
    }
}

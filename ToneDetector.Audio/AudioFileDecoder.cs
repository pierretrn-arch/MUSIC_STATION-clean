using NAudio.Wave;

namespace ToneDetector.Audio;

/// <summary>
/// Decodes an audio file (WAV, AIFF and MP3 through NAudio's own readers; M4A (AAC/ALAC), AAC, WMA,
/// FLAC and anything else through Windows Media Foundation) to a single mono float buffer for
/// offline/batch analysis, reusing the same pipeline as live capture.
/// </summary>
public sealed class AudioFileDecoder
{
    public (float[] Samples, int SampleRate) DecodeToMono(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;

        var samples = new List<float>();
        float[] readBuffer = new float[sampleRate * channels]; // ~1 second per chunk
        int read;
        while ((read = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            for (int i = 0; i < read; i += channels)
            {
                double sum = 0.0;
                for (int c = 0; c < channels; c++)
                    sum += readBuffer[i + c];
                samples.Add((float)(sum / channels));
            }
        }

        return (samples.ToArray(), sampleRate);
    }
}

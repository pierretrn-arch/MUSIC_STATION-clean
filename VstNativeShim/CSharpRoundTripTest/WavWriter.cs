// Minimal 16-bit PCM WAV writer - the C# counterpart to VstNativeShim/src/wavwriter.h, so the
// two milestones' output files are byte-for-byte comparable (same format, same conversion math).
using System.IO;

namespace CSharpRoundTripTest;

internal sealed class WavWriter
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly List<float> _samples = new();

    public WavWriter(int channels, int sampleRate)
    {
        _channels = channels;
        _sampleRate = sampleRate;
    }

    public void AppendBlock(float[] interleaved, int count) => _samples.AddRange(interleaved.Take(count));

    public long TotalFrames => _channels > 0 ? _samples.Count / _channels : 0;

    public void WriteFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        int dataBytes = _samples.Count * sizeof(short);
        int byteRate = _sampleRate * _channels * 2;
        short blockAlign = (short)(_channels * 2);

        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE".ToCharArray());

        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)_channels);
        writer.Write(_sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)16);

        writer.Write("data".ToCharArray());
        writer.Write(dataBytes);

        foreach (float sample in _samples)
        {
            float clamped = Math.Max(-1.0f, Math.Min(1.0f, sample));
            writer.Write((short)(clamped * 32767.0f));
        }
    }
}

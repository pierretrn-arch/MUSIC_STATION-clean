namespace ToneDetector.Core.Dsp;

/// <summary>
/// Converts a buffer of samples from one sample rate to another, so the analysis pipeline can
/// run at a user-chosen rate independent of the capture device's native rate. Downsampling
/// applies a low-pass anti-aliasing pre-filter (cutoff just under the target Nyquist) before
/// linear-interpolation resampling; this favors simplicity/testability over the last word in
/// fidelity, which is an acceptable trade-off since pitch estimation (not audio playback
/// quality) is the consumer.
/// </summary>
public sealed class Resampler
{
    public float[] Resample(ReadOnlySpan<float> input, int inputSampleRate, int outputSampleRate)
    {
        if (inputSampleRate == outputSampleRate)
            return input.ToArray();

        float[] source = input.ToArray();
        if (outputSampleRate < inputSampleRate)
        {
            double antiAliasCutoffHz = outputSampleRate * 0.45;
            var antiAliasFilter = BiquadFilter.LowPass(antiAliasCutoffHz, inputSampleRate);
            for (int i = 0; i < source.Length; i++)
                source[i] = antiAliasFilter.ProcessSample(source[i]);
        }

        double ratio = (double)inputSampleRate / outputSampleRate;
        int outputLength = Math.Max(1, (int)(source.Length / ratio));
        var output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double sourcePosition = i * ratio;
            int index0 = (int)Math.Floor(sourcePosition);
            int index1 = Math.Min(index0 + 1, source.Length - 1);
            double frac = sourcePosition - index0;
            output[i] = (float)(source[index0] * (1.0 - frac) + source[index1] * frac);
        }

        return output;
    }
}

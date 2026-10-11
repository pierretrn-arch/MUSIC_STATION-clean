// Minimal 16-bit PCM WAV writer - just enough to dump rendered VST3 output blocks to a file
// Pierre can inspect (Milestone 2) or actually listen to (Milestone 3+). Not part of the VST3 SDK.
#pragma once

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>
#include <algorithm>

class WavWriter
{
public:
    WavWriter (int numChannels, int sampleRate) : channels (numChannels), rate (sampleRate) {}

    // Interleaved float samples in [-1, 1]; converted to 16-bit PCM on write.
    void AppendBlock (const std::vector<float>& interleaved)
    {
        samples.insert (samples.end (), interleaved.begin (), interleaved.end ());
    }

    bool WriteFile (const std::string& path) const
    {
        FILE* f = nullptr;
        if (fopen_s (&f, path.c_str (), "wb") != 0 || !f)
            return false;

        uint32_t dataBytes = static_cast<uint32_t> (samples.size () * sizeof (int16_t));
        uint32_t byteRate = static_cast<uint32_t> (rate * channels * 2);
        uint16_t blockAlign = static_cast<uint16_t> (channels * 2);
        uint32_t riffSize = 36 + dataBytes;

        fwrite ("RIFF", 1, 4, f);
        fwrite (&riffSize, 4, 1, f);
        fwrite ("WAVE", 1, 4, f);

        fwrite ("fmt ", 1, 4, f);
        uint32_t fmtSize = 16;
        fwrite (&fmtSize, 4, 1, f);
        uint16_t audioFormat = 1; // PCM
        fwrite (&audioFormat, 2, 1, f);
        uint16_t numChannels16 = static_cast<uint16_t> (channels);
        fwrite (&numChannels16, 2, 1, f);
        uint32_t sampleRate32 = static_cast<uint32_t> (rate);
        fwrite (&sampleRate32, 4, 1, f);
        fwrite (&byteRate, 4, 1, f);
        fwrite (&blockAlign, 2, 1, f);
        uint16_t bitsPerSample = 16;
        fwrite (&bitsPerSample, 2, 1, f);

        fwrite ("data", 1, 4, f);
        fwrite (&dataBytes, 4, 1, f);

        for (float s : samples)
        {
            float clamped = std::max (-1.0f, std::min (1.0f, s));
            int16_t pcm = static_cast<int16_t> (clamped * 32767.0f);
            fwrite (&pcm, 2, 1, f);
        }

        fclose (f);
        return true;
    }

    size_t TotalFrames () const { return channels > 0 ? samples.size () / channels : 0; }

private:
    int channels;
    int rate;
    std::vector<float> samples;
};

using System.Buffers.Binary;

namespace Encore.Tests.Fetch;

/// <summary>
/// Tiny valid audio files for import tests (v0.2 design section 15). TagLib reads the length and bitrate from these:
/// an MP3 of CBR MPEG-1 Layer III frames with a zero payload, and a FLAC whose STREAMINFO gives the length.
/// </summary>
internal static class AudioFixtures
{
    private const int SampleRate = 44100;

    /// <summary>Writes an MP3 of <paramref name="seconds"/> at <paramref name="kbps"/> (32-320, a valid MPEG-1 rate).</summary>
    public static string Mp3(string path, double seconds, int kbps = 320)
    {
        int[] rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        var index = Array.IndexOf(rates, kbps);
        if (index <= 0)
            throw new ArgumentOutOfRangeException(nameof(kbps));
        var frameSize = 144 * kbps * 1000 / SampleRate;
        var frames = (int)Math.Round(seconds * SampleRate / 1152);
        var frame = new byte[frameSize];
        frame[0] = 0xFF;
        frame[1] = 0xFB;                    // MPEG-1, Layer III, no CRC
        frame[2] = (byte)(index << 4);      // bitrate index, 44.1 kHz, no padding
        frame[3] = 0x00;                    // stereo
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        for (var i = 0; i < frames; i++)
            stream.Write(frame);
        return path;
    }

    /// <summary>Writes a FLAC of <paramref name="seconds"/> (44.1 kHz, 16-bit stereo) with a little fake frame data.</summary>
    public static string Flac(string path, double seconds, int audioBytes = 4096)
    {
        var samples = (long)Math.Round(seconds * SampleRate);
        var info = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(0), 4096);   // min block size
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), 4096);   // max block size
        // min/max frame size (6 bytes) stay 0 = unknown
        var packed = ((ulong)SampleRate << 44) | (1UL << 41) | (15UL << 36) | (ulong)samples;
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10), packed);
        // MD5 (16 bytes) stays 0 = unknown

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.Write("fLaC"u8);
        stream.Write([0x80, 0x00, 0x00, 34]);   // last metadata block, STREAMINFO, length 34
        stream.Write(info);
        var audio = new byte[audioBytes];
        audio[0] = 0xFF;
        audio[1] = 0xF8;
        stream.Write(audio);
        return path;
    }
}

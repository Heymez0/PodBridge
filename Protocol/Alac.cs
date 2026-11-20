namespace PodBridge.Protocol;

/// <summary>Uncompressed ALAC frame. Bit layout: 3b CPE=1, 4b 0, 12b 0, 1b hasSize=0,
/// 2b 0, 1b isNotCompressed=1, samples (MSB first), 3b END=7, byte alignment.</summary>
public static class Alac
{
    private const int HeaderBits = 23;
    private const int EndBits = 3;

    public static int FrameSize(int samples) => (HeaderBits + samples * 16 + EndBits + 7) / 8;

    /// <summary>Allocation-free; called from the audio thread. Because the header is 23 bits,
    /// each sample is shifted by 7 bits and spans two bytes plus the top bit of the next byte.</summary>
    public static int Encode(ReadOnlySpan<short> interleaved, Span<byte> output)
    {
        output[0] = 0x20;
        output[1] = 0;
        int pos = 2;
        int carry = 0x02;   // isNotCompressed; the lowest bit is the first sample's most significant bit
        foreach (short sample in interleaved)
        {
            ushort s = (ushort)sample;
            output[pos++] = (byte)(carry | s >> 15);
            output[pos++] = (byte)(s >> 7);
            carry = (s & 0x7F) << 1;
        }
        output[pos++] = (byte)(carry | 1);   // first bit of the END tag
        output[pos++] = 0xC0;                // remaining two bits of the END tag plus padding
        return pos;
    }
}

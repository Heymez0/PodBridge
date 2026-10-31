namespace PodBridge.Protocol;

/// <summary>Sıkıştırılmamış ALAC çerçevesi. Bit düzeni: 3b CPE=1, 4b 0, 12b 0, 1b hasSize=0,
/// 2b 0, 1b isNotCompressed=1, örnekler (MSB önce), 3b END=7, bayt hizalama.</summary>
public static class Alac
{
    private const int HeaderBits = 23;
    private const int EndBits = 3;

    public static int FrameSize(int samples) => (HeaderBits + samples * 16 + EndBits + 7) / 8;

    /// <summary>Ayırma yapmaz, ses iş parçacığından çağrılır. Başlık 23 bit olduğu için
    /// her örnek 7 bit kaymış durumda iki bayta ve bir sonraki baytın üst bitlerine yayılır.</summary>
    public static int Encode(ReadOnlySpan<short> interleaved, Span<byte> output)
    {
        output[0] = 0x20;
        output[1] = 0;
        int pos = 2;
        int carry = 0x02;   // isNotCompressed; en düşük bit ilk örneğin en anlamlı bitidir
        foreach (short sample in interleaved)
        {
            ushort s = (ushort)sample;
            output[pos++] = (byte)(carry | s >> 15);
            output[pos++] = (byte)(s >> 7);
            carry = (s & 0x7F) << 1;
        }
        output[pos++] = (byte)(carry | 1);   // END etiketinin ilk biti
        output[pos++] = 0xC0;                // END etiketinin kalan iki biti ve dolgu
        return pos;
    }
}

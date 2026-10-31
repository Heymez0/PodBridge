namespace PodBridge.Protocol;

/// <summary>HomeKit TLV8: 255 baytı aşan değerler aynı etiketle parçalanır.</summary>
public static class Tlv8
{
    public const byte Method = 0x00, Salt = 0x02, PublicKey = 0x03, Proof = 0x04,
                      State = 0x06, Error = 0x07, Flags = 0x13;

    public static byte[] Encode(params (byte Tag, byte[] Value)[] items)
    {
        using var ms = new MemoryStream();
        foreach (var (tag, value) in items)
        {
            int pos = 0;
            do
            {
                int chunk = Math.Min(255, value.Length - pos);
                ms.WriteByte(tag);
                ms.WriteByte((byte)chunk);
                ms.Write(value, pos, chunk);
                pos += chunk;
            } while (pos < value.Length);
        }
        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        int i = 0;
        int lastTag = -1;
        while (i + 2 <= data.Length)
        {
            byte tag = data[i];
            int len = data[i + 1];
            if (i + 2 + len > data.Length) break;
            var chunk = data.Slice(i + 2, len).ToArray();
            result[tag] = tag == lastTag ? [.. result[tag], .. chunk] : chunk;
            lastTag = tag;
            i += 2 + len;
        }
        return result;
    }
}

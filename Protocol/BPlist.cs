using System.Buffers.Binary;
using System.Text;

namespace PodBridge.Protocol;

/// <summary>AirPlay SETUP için en küçük bplist00 kodlayıcı ve çözücü.
/// Değerler: string, long, bool, byte[], List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.</summary>
public static class BPlist
{
    private const int MaxObjects = 4096;
    private const int MaxDepth = 16;

    public static byte[] Encode(object root)
    {
        var objects = new List<object>();
        Collect(root, objects);
        int refSize = objects.Count < 256 ? 1 : 2;

        using var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            WriteObject(ms, objects[i], objects, refSize);
        }

        long tableStart = ms.Position;
        int offsetSize = tableStart < 256 ? 1 : tableStart < 65536 ? 2 : 4;
        foreach (long offset in offsets) WriteBE(ms, offset, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteInt64BigEndian(trailer[8..], objects.Count);
        BinaryPrimitives.WriteInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteInt64BigEndian(trailer[24..], tableStart);
        ms.Write(trailer);
        return ms.ToArray();
    }

    // Her nesne, üst nesneden sonra tabloya girer; kök her zaman 0. indekstedir.
    private static void Collect(object value, List<object> objects)
    {
        objects.Add(value);
        switch (value)
        {
            case Dictionary<string, object> dict:
                foreach (var key in dict.Keys) objects.Add(key);
                foreach (var item in dict.Values) Collect(item, objects);
                break;
            case List<object> list:
                foreach (var item in list) Collect(item, objects);
                break;
        }
    }

    private static int IndexAfter(object parent, int childOrdinal, List<object> objects)
    {
        int index = objects.FindIndex(o => ReferenceEquals(o, parent)) + 1;
        if (parent is Dictionary<string, object> dict)
        {
            index += dict.Count;
            foreach (var item in dict.Values)
            {
                if (childOrdinal-- == 0) return index;
                index += CountTree(item);
            }
        }
        else if (parent is List<object> list)
        {
            foreach (var item in list)
            {
                if (childOrdinal-- == 0) return index;
                index += CountTree(item);
            }
        }
        throw new InvalidOperationException();
    }

    private static int CountTree(object value) => value switch
    {
        Dictionary<string, object> d => 1 + d.Count + d.Values.Sum(CountTree),
        List<object> l => 1 + l.Sum(CountTree),
        _ => 1,
    };

    private static void WriteObject(Stream s, object value, List<object> objects, int refSize)
    {
        switch (value)
        {
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case int i:
                WriteInt(s, i);
                break;
            case long l:
                WriteInt(s, l);
                break;
            case string str:
                bool ascii = str.All(c => c < 128);
                var bytes = ascii ? Encoding.ASCII.GetBytes(str) : Encoding.BigEndianUnicode.GetBytes(str);
                WriteMarker(s, ascii ? 0x5 : 0x6, ascii ? bytes.Length : bytes.Length / 2);
                s.Write(bytes);
                break;
            case byte[] data:
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            case List<object> list:
                WriteMarker(s, 0xA, list.Count);
                for (int n = 0; n < list.Count; n++)
                    WriteBE(s, IndexAfter(list, n, objects), refSize);
                break;
            case Dictionary<string, object> dict:
                WriteMarker(s, 0xD, dict.Count);
                int self = objects.FindIndex(o => ReferenceEquals(o, dict));
                for (int n = 0; n < dict.Count; n++) WriteBE(s, self + 1 + n, refSize);
                for (int n = 0; n < dict.Count; n++) WriteBE(s, IndexAfter(dict, n, objects), refSize);
                break;
            default:
                throw new ArgumentException($"Desteklenmeyen tür: {value.GetType()}");
        }
    }

    private static void WriteInt(Stream s, long v)
    {
        if (v is >= 0 and <= 0xFF) { s.WriteByte(0x10); WriteBE(s, v, 1); }
        else if (v is >= 0 and <= 0xFFFF) { s.WriteByte(0x11); WriteBE(s, v, 2); }
        else if (v is >= 0 and <= 0xFFFFFFFFL) { s.WriteByte(0x12); WriteBE(s, v, 4); }
        else { s.WriteByte(0x13); WriteBE(s, v, 8); }
    }

    private static void WriteMarker(Stream s, int kind, int count)
    {
        if (count < 15) { s.WriteByte((byte)(kind << 4 | count)); return; }
        s.WriteByte((byte)(kind << 4 | 0xF));
        WriteInt(s, count);
    }

    private static void WriteBE(Stream s, long v, int size)
    {
        for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }

    public static object? Decode(byte[] data)
    {
        if (data.Length < 40 || !data.AsSpan(0, 8).SequenceEqual("bplist00"u8))
            throw new FormatException("bplist00 değil");

        var trailer = data.AsSpan(data.Length - 32);
        int offsetSize = trailer[6], refSize = trailer[7];
        long count = BinaryPrimitives.ReadInt64BigEndian(trailer[8..]);
        long top = BinaryPrimitives.ReadInt64BigEndian(trailer[16..]);
        long table = BinaryPrimitives.ReadInt64BigEndian(trailer[24..]);
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || count is <= 0 or > MaxObjects
            || top < 0 || top >= count || table < 8 || table + count * offsetSize > data.Length - 32)
            throw new FormatException("Geçersiz bplist üstbilgisi");

        return ReadObject(data, table, offsetSize, refSize, count, top, 0);
    }

    private static long ReadBE(ReadOnlySpan<byte> d, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = v << 8 | d[i];
        return v;
    }

    private static object? ReadObject(byte[] d, long table, int offSize, int refSize, long count, long index, int depth)
    {
        if (depth > MaxDepth || index < 0 || index >= count) throw new FormatException("Geçersiz bplist başvurusu");
        long pos = ReadBE(d.AsSpan((int)(table + index * offSize)), offSize);
        if (pos < 8 || pos >= table) throw new FormatException("Geçersiz bplist konumu");

        byte marker = d[pos];
        int kind = marker >> 4, info = marker & 0xF;
        long p = pos + 1;

        long ReadCount()
        {
            if (info != 0xF) return info;
            int size = 1 << (d[p] & 0xF);
            long c = ReadBE(d.AsSpan((int)p + 1), size);
            p += 1 + size;
            if (c < 0 || c > d.Length) throw new FormatException("Geçersiz bplist uzunluğu");
            return c;
        }

        switch (kind)
        {
            case 0x0: return info == 9 ? true : info == 8 ? false : null;
            case 0x1:
                {
                    int size = 1 << info;
                    if (size > 8) throw new FormatException("Çok büyük tamsayı");
                    return ReadBE(d.AsSpan((int)p), size);
                }
            case 0x2:
                return info == 3 ? BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)p))
                                 : (double)BinaryPrimitives.ReadSingleBigEndian(d.AsSpan((int)p));
            case 0x3: return BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)p));
            case 0x4: { long c = ReadCount(); return d.AsSpan((int)p, (int)c).ToArray(); }
            case 0x5: { long c = ReadCount(); return Encoding.ASCII.GetString(d, (int)p, (int)c); }
            case 0x6: { long c = ReadCount(); return Encoding.BigEndianUnicode.GetString(d, (int)p, (int)c * 2); }
            case 0x7: { long c = ReadCount(); return Encoding.UTF8.GetString(d, (int)p, (int)c); }
            case 0xA:
                {
                    long c = ReadCount();
                    var list = new List<object?>((int)Math.Min(c, 256));
                    for (long i = 0; i < c; i++)
                    {
                        long r = ReadBE(d.AsSpan((int)(p + i * refSize)), refSize);
                        list.Add(ReadObject(d, table, offSize, refSize, count, r, depth + 1));
                    }
                    return list;
                }
            case 0xD:
                {
                    long c = ReadCount();
                    var dict = new Dictionary<string, object?>();
                    for (long i = 0; i < c; i++)
                    {
                        long kr = ReadBE(d.AsSpan((int)(p + i * refSize)), refSize);
                        long vr = ReadBE(d.AsSpan((int)(p + (c + i) * refSize)), refSize);
                        if (ReadObject(d, table, offSize, refSize, count, kr, depth + 1) is string key)
                            dict[key] = ReadObject(d, table, offSize, refSize, count, vr, depth + 1);
                    }
                    return dict;
                }
            default:
                return null;
        }
    }
}

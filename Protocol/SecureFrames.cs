using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PodBridge.Protocol;

/// <summary>HAP frame: [2-byte LE length][ciphertext][16-byte tag].
/// The length bytes are the associated data; the nonce is a separate 64-bit counter per direction.</summary>
public sealed class SecureFrames : IDisposable
{
    private const int MaxChunk = 1024;
    private const int TagSize = 16;

    private readonly ChaCha20Poly1305 _writer;
    private readonly ChaCha20Poly1305 _reader;
    private ulong _sendCounter;
    private ulong _receiveCounter;
    private readonly List<byte> _pending = [];

    public SecureFrames(byte[] writeKey, byte[] readKey)
    {
        _writer = new ChaCha20Poly1305(writeKey);
        _reader = new ChaCha20Poly1305(readKey);
    }

    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        int chunks = Math.Max(1, (plain.Length + MaxChunk - 1) / MaxChunk);
        var output = new byte[plain.Length + chunks * (2 + TagSize)];
        int pos = 0;
        Span<byte> nonce = stackalloc byte[12];
        while (!plain.IsEmpty)
        {
            int len = Math.Min(MaxChunk, plain.Length);
            var lenBytes = output.AsSpan(pos, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)len);
            WriteNonce(nonce, _sendCounter++);
            _writer.Encrypt(nonce, plain[..len], output.AsSpan(pos + 2, len),
                            output.AsSpan(pos + 2 + len, TagSize), lenBytes);
            pos += 2 + len + TagSize;
            plain = plain[len..];
        }
        return output[..pos];
    }

    /// <summary>Buffers incoming bytes and returns the plaintext of every completed frame.</summary>
    public byte[] Open(ReadOnlySpan<byte> cipher)
    {
        _pending.AddRange(cipher);
        using var plain = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        int pos = 0;
        var buffer = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
        while (buffer.Length - pos >= 2)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(buffer[pos..]);
            if (buffer.Length - pos < 2 + len + TagSize) break;
            WriteNonce(nonce, _receiveCounter++);
            var output = new byte[len];
            _reader.Decrypt(nonce, buffer.Slice(pos + 2, len), buffer.Slice(pos + 2 + len, TagSize),
                            output, buffer.Slice(pos, 2));
            plain.Write(output);
            pos += 2 + len + TagSize;
        }
        _pending.RemoveRange(0, pos);
        return plain.ToArray();
    }

    private static void WriteNonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
    }
}

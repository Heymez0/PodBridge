using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace PodBridge.Protocol;

/// <summary>SRP-6a client for HAP pairing (RFC 5054 3072-bit group, SHA-512).</summary>
public sealed class SrpClient
{
    private const string Username = "Pair-Setup";
    private const int NBytes = 384;

    public static readonly BigInteger N = new(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF"),
        isUnsigned: true, isBigEndian: true);

    public static readonly BigInteger G = 5;

    private readonly BigInteger _a;
    private readonly BigInteger _publicA;

    public byte[] PublicA { get; }
    public byte[] ProofM1 { get; private set; } = [];
    public byte[] SessionKey { get; private set; } = [];

    public SrpClient() : this(RandomNumberGenerator.GetBytes(32)) { }

    internal SrpClient(byte[] secret)
    {
        _a = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
        _publicA = BigInteger.ModPow(G, _a, N);
        PublicA = ToBytes(_publicA);
    }

    public bool Process(string pin, byte[] salt, byte[] serverB)
    {
        var b = new BigInteger(serverB, isUnsigned: true, isBigEndian: true);
        if (b % N == 0) return false;

        var k = ToInt(SHA512.HashData([.. Pad(N), .. Pad(G)]));
        var u = ToInt(SHA512.HashData([.. Pad(_publicA), .. Pad(b)]));
        if (u == 0) return false;

        var inner = SHA512.HashData(Encoding.UTF8.GetBytes($"{Username}:{pin}"));
        var x = ToInt(SHA512.HashData([.. salt, .. inner]));

        var kgx = k * BigInteger.ModPow(G, x, N) % N;
        var @base = ((b - kgx) % N + N) % N;
        var s = BigInteger.ModPow(@base, _a + u * x, N);
        SessionKey = SHA512.HashData(ToBytes(s));

        var hN = SHA512.HashData(ToBytes(N));
        var hG = SHA512.HashData(ToBytes(G));
        var xor = new byte[hN.Length];
        for (int i = 0; i < xor.Length; i++) xor[i] = (byte)(hN[i] ^ hG[i]);
        var hI = SHA512.HashData(Encoding.UTF8.GetBytes(Username));
        ProofM1 = SHA512.HashData([.. xor, .. hI, .. salt, .. PublicA, .. ToBytes(b), .. SessionKey]);
        return true;
    }

    public bool VerifyServerProof(byte[] m2) =>
        CryptographicOperations.FixedTimeEquals(
            SHA512.HashData([.. PublicA, .. ProofM1, .. SessionKey]), m2);

    private static BigInteger ToInt(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] Pad(BigInteger v)
    {
        var raw = ToBytes(v);
        var padded = new byte[NBytes];
        raw.CopyTo(padded, NBytes - raw.Length);
        return padded;
    }
}

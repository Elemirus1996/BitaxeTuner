using System.Numerics;

namespace BitaxeTuner.Core.Fans;

/// <summary>
/// X25519 nach RFC 7748 (Montgomery-Leiter mit <see cref="BigInteger"/>) – nur für die Pico-Kopplung (0.9.23), .NET 8
/// bringt X25519 nicht mit. Der private Schlüssel ist je Kopplung neu und wird danach verworfen.
/// </summary>
public static class X25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>Basispunkt u = 9.</summary>
    public static byte[] BasePoint
    {
        get
        {
            var b = new byte[32];
            b[0] = 9;
            return b;
        }
    }

    public static byte[] ScalarMult(byte[] scalar, byte[] u)
    {
        if (scalar.Length != 32 || u.Length != 32) throw new ArgumentException("X25519: 32 Byte erwartet.");
        var k = (byte[])scalar.Clone();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        var kn = new BigInteger(k, isUnsigned: true, isBigEndian: false);
        var ub = (byte[])u.Clone();
        ub[31] &= 127;
        var x1 = new BigInteger(ub, isUnsigned: true, isBigEndian: false) % P;
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (var t = 254; t >= 0; t--)
        {
            var kt = (int)((kn >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;
            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + 121665 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        var r = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        var bytes = r.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = new byte[32];
        Array.Copy(bytes, result, Math.Min(32, bytes.Length));
        return result;
    }

    private static BigInteger Mod(BigInteger v)
    {
        var m = v % P;
        return m.Sign < 0 ? m + P : m;
    }
}

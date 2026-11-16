namespace ShardForge.Core.Uop;

/// <summary>
/// Filename hash used by Mythic Package (UOP) files: Bob Jenkins' lookup3 "hashlittle2",
/// returning (primary &lt;&lt; 32) | secondary as the client does.
/// </summary>
public static class UopHash
{
    public static ulong Hash(string name)
    {
        // UOP names are ASCII, lower-case, forward slashes.
        ReadOnlySpan<char> s = name;
        int length = s.Length;
        uint a, b, c;
        a = b = c = 0xDEADBEEF + (uint)length;

        int i = 0;
        while (length - i > 12)
        {
            a += Word(s, i);
            b += Word(s, i + 4);
            c += Word(s, i + 8);

            a -= c; a ^= Rot(c, 4); c += b;
            b -= a; b ^= Rot(a, 6); a += c;
            c -= b; c ^= Rot(b, 8); b += a;
            a -= c; a ^= Rot(c, 16); c += b;
            b -= a; b ^= Rot(a, 19); a += c;
            c -= b; c ^= Rot(b, 4); b += a;
            i += 12;
        }

        int remaining = length - i;
        if (remaining == 0)
        {
            return ((ulong)c << 32) | 0; // matches the reference implementation for empty tails
        }

        for (int k = remaining - 1; k >= 0; k--)
        {
            uint v = (uint)(byte)s[i + k] << (8 * (k % 4));
            if (k >= 8) c += v;
            else if (k >= 4) b += v;
            else a += v;
        }

        c ^= b; c -= Rot(b, 14);
        a ^= c; a -= Rot(c, 11);
        b ^= a; b -= Rot(a, 25);
        c ^= b; c -= Rot(b, 16);
        a ^= c; a -= Rot(c, 4);
        b ^= a; b -= Rot(a, 14);
        c ^= b; c -= Rot(b, 24);

        return ((ulong)b << 32) | c;
    }

    private static uint Word(ReadOnlySpan<char> s, int i) =>
        (byte)s[i] | ((uint)(byte)s[i + 1] << 8) | ((uint)(byte)s[i + 2] << 16) | ((uint)(byte)s[i + 3] << 24);

    private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));
}

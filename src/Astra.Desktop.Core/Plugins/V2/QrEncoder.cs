using System.Text;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>
/// A compact QR encoder (ISO/IEC 18004) for the <c>QRCode</c> component: byte mode, error correction M, versions 1–10, the
/// mask with the lowest penalty. The same algorithm as the web shell's (web/src/plugin-ui/widgets.tsx), so both draw the
/// same code for the same text. Returns null when the text doesn't fit version 10.
/// </summary>
public static class QrEncoder
{
    private static readonly int[][] EcM =
    [
        [10, 1, 16, 0, 0], [16, 1, 28, 0, 0], [26, 1, 44, 0, 0], [18, 2, 32, 0, 0], [24, 2, 43, 0, 0],
        [16, 4, 27, 0, 0], [18, 4, 31, 0, 0], [22, 2, 38, 2, 39], [22, 3, 36, 2, 37], [26, 4, 43, 1, 44],
    ];

    private static readonly int[][] Align = [[], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]];

    public static bool[,]? Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var version = 0;
        for (var v = 1; v <= 10; v++)
        {
            var e = EcM[v - 1];
            var capacity = e[1] * e[2] + e[3] * e[4];
            if (4 + (v < 10 ? 8 : 16) + data.Length * 8 <= capacity * 8)
            {
                version = v;
                break;
            }
        }
        if (version == 0) return null;
        var (ecLen, b1, c1, b2, c2) = (EcM[version - 1][0], EcM[version - 1][1], EcM[version - 1][2], EcM[version - 1][3], EcM[version - 1][4]);
        var total = b1 * c1 + b2 * c2;
        var bits = new List<int>();
        void Put(int value, int n)
        {
            for (var i = n - 1; i >= 0; i--) bits.Add((value >> i) & 1);
        }
        Put(4, 4);
        Put(data.Length, version < 10 ? 8 : 16);
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, total * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(0);
        var cw = new List<int>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            var b = 0;
            for (var j = 0; j < 8; j++) b = (b << 1) | bits[i + j];
            cw.Add(b);
        }
        for (var p = 0; cw.Count < total; p++) cw.Add(p % 2 == 1 ? 0x11 : 0xec);

        var exp = new int[512];
        var log = new int[256];
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            exp[i] = x;
            log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11d;
        }
        for (var i = 255; i < 512; i++) exp[i] = exp[i - 255];
        int Mul(int a, int b) => a != 0 && b != 0 ? exp[log[a] + log[b]] : 0;
        var gen = new List<int> { 1 };
        for (var i = 0; i < ecLen; i++)
        {
            var next = new int[gen.Count + 1];
            for (var j = 0; j < gen.Count; j++)
            {
                next[j] ^= gen[j];
                next[j + 1] ^= Mul(gen[j], exp[i]);
            }
            gen = [.. next];
        }
        var blocks = new List<int[]>();
        var at = 0;
        for (var i = 0; i < b1 + b2; i++)
        {
            var n = i < b1 ? c1 : c2;
            blocks.Add(cw.Skip(at).Take(n).ToArray());
            at += n;
        }
        var ecc = blocks.Select(b =>
        {
            var r = new int[b.Length + ecLen];
            b.CopyTo(r, 0);
            for (var i = 0; i < b.Length; i++)
            {
                var f = r[i];
                if (f != 0) for (var j = 0; j < gen.Count; j++) r[i + j] ^= Mul(gen[j], f);
            }
            return r.Skip(b.Length).ToArray();
        }).ToList();
        var final = new List<int>();
        for (var i = 0; i < Math.Max(c1, c2); i++) foreach (var b in blocks) if (i < b.Length) final.Add(b[i]);
        for (var i = 0; i < ecLen; i++) foreach (var e in ecc) final.Add(e[i]);

        var size = version * 4 + 17;
        var m = new bool?[size, size];
        var fn = new bool[size, size];
        void Set(int r, int c, bool v)
        {
            m[r, c] = v;
            fn[r, c] = true;
        }
        void Finder(int r, int c)
        {
            for (var i = -1; i <= 7; i++)
                for (var j = -1; j <= 7; j++)
                {
                    if (r + i < 0 || r + i >= size || c + j < 0 || c + j >= size) continue;
                    var on = i is >= 0 and <= 6 && j is >= 0 and <= 6 && (i is 0 or 6 || j is 0 or 6 || (i is >= 2 and <= 4 && j is >= 2 and <= 4));
                    Set(r + i, c + j, on);
                }
        }
        Finder(0, 0);
        Finder(0, size - 7);
        Finder(size - 7, 0);
        for (var i = 8; i < size - 8; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        var al = Align[version - 1];
        foreach (var r in al)
            foreach (var c in al)
            {
                if (fn[r, c]) continue;
                for (var i = -2; i <= 2; i++)
                    for (var j = -2; j <= 2; j++) Set(r + i, c + j, Math.Max(Math.Abs(i), Math.Abs(j)) != 1);
            }
        Set(size - 8, 8, true);
        for (var i = 0; i < 9; i++)
        {
            if (!fn[8, i]) Set(8, i, false);
            if (!fn[i, 8]) Set(i, 8, false);
        }
        for (var i = 0; i < 8; i++)
        {
            Set(8, size - 1 - i, false);
            Set(size - 1 - i, 8, false);
        }
        if (version >= 7)
        {
            var rem = version;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1f25);
            var v = (version << 12) | rem;
            for (var i = 0; i < 18; i++)
            {
                var bit = ((v >> i) & 1) == 1;
                Set(size - 11 + i % 3, i / 3, bit);
                Set(i / 3, size - 11 + i % 3, bit);
            }
        }
        var dataBits = new List<int>();
        foreach (var b in final)
            for (var i = 7; i >= 0; i--) dataBits.Add((b >> i) & 1);
        var k = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var c = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var r = upward ? size - 1 - vert : vert;
                    if (fn[r, c]) continue;
                    m[r, c] = k < dataBits.Count && dataBits[k++] == 1;
                }
        }
        Func<int, int, bool>[] masks =
        [
            (r, c) => (r + c) % 2 == 0, (r, _) => r % 2 == 0, (_, c) => c % 3 == 0, (r, c) => (r + c) % 3 == 0,
            (r, c) => (r / 2 + c / 3) % 2 == 0, (r, c) => (r * c) % 2 + (r * c) % 3 == 0,
            (r, c) => ((r * c) % 2 + (r * c) % 3) % 2 == 0, (r, c) => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
        ];
        bool[,]? best = null;
        var bestScore = int.MaxValue;
        for (var mi = 0; mi < masks.Length; mi++)
        {
            var g = new bool[size, size];
            for (var r = 0; r < size; r++)
                for (var c = 0; c < size; c++)
                    g[r, c] = fn[r, c] ? m[r, c] == true : (m[r, c] == true) != masks[mi](r, c);
            var f = mi;
            var rem = f;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            f = ((f << 10) | rem) ^ 0x5412;
            bool Bit(int i) => ((f >> i) & 1) == 1;
            for (var i = 0; i <= 5; i++) g[i, 8] = Bit(i);
            g[7, 8] = Bit(6);
            g[8, 8] = Bit(7);
            g[8, 7] = Bit(8);
            for (var i = 9; i < 15; i++) g[8, 14 - i] = Bit(i);
            for (var i = 0; i < 8; i++) g[8, size - 1 - i] = Bit(i);
            for (var i = 8; i < 15; i++) g[size - 15 + i, 8] = Bit(i);
            g[size - 8, 8] = true;
            var score = 0;
            for (var r = 0; r < size; r++)
            {
                var run = 1;
                for (var c = 1; c < size; c++)
                {
                    if (g[r, c] == g[r, c - 1]) run++;
                    else
                    {
                        if (run >= 5) score += run - 2;
                        run = 1;
                    }
                }
                if (run >= 5) score += run - 2;
            }
            for (var r = 0; r < size - 1; r++)
                for (var c = 0; c < size - 1; c++)
                    if (g[r, c] == g[r, c + 1] && g[r, c] == g[r + 1, c] && g[r, c] == g[r + 1, c + 1]) score += 3;
            if (score < bestScore)
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }
}

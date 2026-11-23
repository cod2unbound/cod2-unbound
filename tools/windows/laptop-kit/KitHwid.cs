using System;
using System.Text;

// CoD2x v1.4.6.8 passes the machine description to retail CL_BuildMD5.
// This implementation uses the RFC 1321 compression algorithm with the
// two retail initial states. It neither sets a game identifier nor logs one.
public static class KitHwid
{
    private static readonly int[,] Rotations = {
        { 7, 12, 17, 22 }, { 5, 9, 14, 20 }, { 4, 11, 16, 23 }, { 6, 10, 15, 21 }
    };

    public static string Hash(byte[] description)
    {
        string first = Digest(description, new uint[] { 0x6f1cd602, 0x226c74be, 0xb31c088d, 0x555a9639 });
        return Digest(Encoding.ASCII.GetBytes(first), new uint[] { 0x677d44b3, 0xf137f963, 0x9977ab6c, 0x12214f0c });
    }

    private static string Digest(byte[] input, uint[] state)
    {
        byte[] padded = new byte[((input.Length + 72) / 64) * 64];
        Buffer.BlockCopy(input, 0, padded, 0, input.Length);
        padded[input.Length] = 0x80;
        Buffer.BlockCopy(BitConverter.GetBytes((ulong)input.Length * 8), 0, padded, padded.Length - 8, 8);
        try {
            unchecked {
                for (int offset = 0; offset < padded.Length; offset += 64) {
                    uint a = state[0], b = state[1], c = state[2], d = state[3];
                    for (int i = 0; i < 64; i++) {
                        int group = i / 16;
                        uint f;
                        int word;
                        switch (group) {
                            case 0: f = (b & c) | (~b & d); word = i; break;
                            case 1: f = (d & b) | (~d & c); word = (5 * i + 1) % 16; break;
                            case 2: f = b ^ c ^ d; word = (3 * i + 5) % 16; break;
                            default: f = c ^ (b | ~d); word = (7 * i) % 16; break;
                        }
                        uint value = a + f + (uint)(4294967296.0 * Math.Abs(Math.Sin(i + 1)))
                            + BitConverter.ToUInt32(padded, offset + word * 4);
                        int shift = Rotations[group, i % 4];
                        a = d;
                        d = c;
                        c = b;
                        b += (value << shift) | (value >> (32 - shift));
                    }
                    state[0] += a; state[1] += b; state[2] += c; state[3] += d;
                }
            }
            var hex = new StringBuilder(32);
            foreach (uint value in state)
                foreach (byte component in BitConverter.GetBytes(value))
                    hex.Append(component.ToString("x2"));
            return hex.ToString();
        } finally {
            Array.Clear(padded, 0, padded.Length);
        }
    }
}

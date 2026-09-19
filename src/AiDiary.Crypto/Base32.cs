namespace AiDiary.Crypto;

/// <summary>RFC 4648 base32, uppercase, no padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result[index++] = Alphabet[(buffer >> (bits - 5)) & 0x1F];
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            result[index++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        }
        return new string(result, 0, index);
    }

    public static byte[] Decode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clean = input.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        var output = new byte[clean.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"Invalid base32 character: '{c}'.");
            }
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }
        return output;
    }
}

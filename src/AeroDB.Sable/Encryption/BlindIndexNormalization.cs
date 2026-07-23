using System.Security.Cryptography;
using System.Text;

namespace AeroDB.Sable;

internal static class BlindIndexNormalization
{
    internal static byte[] Normalize(string value, BlindIndexNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(value);

        return normalizer switch
        {
            BlindIndexNormalizer.UsSocialSecurityNumberV1 => NormalizeSsn(value),
            _ => throw new SableBlindIndexConfigurationException(
                $"Blind-index normalizer '{normalizer}' is not supported.")
        };
    }

    internal static bool FixedTimeEquals(
        string left,
        string right,
        BlindIndexNormalizer normalizer)
    {
        var leftBytes = Normalize(left, normalizer);
        var rightBytes = Normalize(right, normalizer);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static byte[] NormalizeSsn(string value)
    {
        Span<byte> digits = stackalloc byte[9];
        var count = 0;

        foreach (var character in value)
        {
            if (character is >= '0' and <= '9')
            {
                if (count == digits.Length)
                    throw InvalidSsn();
                digits[count++] = (byte)character;
                continue;
            }

            if (character is '-' or ' ')
                continue;

            throw InvalidSsn();
        }

        if (count != digits.Length)
            throw InvalidSsn();

        return digits.ToArray();
    }

    private static SableBlindIndexNormalizationException InvalidSsn()
        => new(
            "The UsSocialSecurityNumberV1 blind-index normalizer requires exactly nine ASCII digits " +
            "and permits only spaces or hyphens as presentation characters.");
}

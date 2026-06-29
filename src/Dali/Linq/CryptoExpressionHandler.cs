namespace Dali;

/// <summary>
/// Transforms <see cref="SurrealCryptoFunctions"/> marker class method calls into SurrealQL crypto:: function expressions.
/// </summary>
internal static class CryptoExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealCryptoFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateCryptoFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            // ── Password hashing ──
            "Argon2Generate" when args.Length == 1 => $"crypto::argon2::generate({args[0]})",
            "Argon2Compare" when args.Length == 2 => $"crypto::argon2::compare({args[0]}, {args[1]})",
            "BcryptGenerate" when args.Length == 1 => $"crypto::bcrypt::generate({args[0]})",
            "BcryptCompare" when args.Length == 2 => $"crypto::bcrypt::compare({args[0]}, {args[1]})",
            "Pbkdf2Generate" when args.Length == 1 => $"crypto::pbkdf2::generate({args[0]})",
            "Pbkdf2Compare" when args.Length == 2 => $"crypto::pbkdf2::compare({args[0]}, {args[1]})",
            "ScryptGenerate" when args.Length == 1 => $"crypto::scrypt::generate({args[0]})",
            "ScryptCompare" when args.Length == 2 => $"crypto::scrypt::compare({args[0]}, {args[1]})",

            // ── General-purpose hashing ──
            "Md5" when args.Length == 1 => $"crypto::md5({args[0]})",
            "Sha1" when args.Length == 1 => $"crypto::sha1({args[0]})",
            "Sha256" when args.Length == 1 => $"crypto::sha256({args[0]})",
            "Sha512" when args.Length == 1 => $"crypto::sha512({args[0]})",

            _ => null
        };
    }
}

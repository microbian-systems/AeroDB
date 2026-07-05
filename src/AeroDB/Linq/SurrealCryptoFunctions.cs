namespace AeroDB;

/// <summary>
/// SurrealDB crypto functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealCryptoFunctions
{
    // ── Password hashing: generate ──

    /// <summary>Generates an Argon2 password hash. Maps to <c>crypto::argon2::generate(pass)</c>.</summary>
    public static string Argon2Generate(string pass) => throw new NotSupportedException("SurrealCryptoFunctions.Argon2Generate can only be used inside a LINQ expression.");
    /// <summary>Compares a password against an Argon2 hash. Maps to <c>crypto::argon2::compare(pass, hash)</c>.</summary>
    public static bool Argon2Compare(string pass, string hash) => throw new NotSupportedException("SurrealCryptoFunctions.Argon2Compare can only be used inside a LINQ expression.");
    /// <summary>Generates a Bcrypt password hash. Maps to <c>crypto::bcrypt::generate(pass)</c>.</summary>
    public static string BcryptGenerate(string pass) => throw new NotSupportedException("SurrealCryptoFunctions.BcryptGenerate can only be used inside a LINQ expression.");
    /// <summary>Compares a password against a Bcrypt hash. Maps to <c>crypto::bcrypt::compare(pass, hash)</c>.</summary>
    public static bool BcryptCompare(string pass, string hash) => throw new NotSupportedException("SurrealCryptoFunctions.BcryptCompare can only be used inside a LINQ expression.");
    /// <summary>Generates a PBKDF2 password hash. Maps to <c>crypto::pbkdf2::generate(pass)</c>.</summary>
    public static string Pbkdf2Generate(string pass) => throw new NotSupportedException("SurrealCryptoFunctions.Pbkdf2Generate can only be used inside a LINQ expression.");
    /// <summary>Compares a password against a PBKDF2 hash. Maps to <c>crypto::pbkdf2::compare(pass, hash)</c>.</summary>
    public static bool Pbkdf2Compare(string pass, string hash) => throw new NotSupportedException("SurrealCryptoFunctions.Pbkdf2Compare can only be used inside a LINQ expression.");
    /// <summary>Generates a Scrypt password hash. Maps to <c>crypto::scrypt::generate(pass)</c>.</summary>
    public static string ScryptGenerate(string pass) => throw new NotSupportedException("SurrealCryptoFunctions.ScryptGenerate can only be used inside a LINQ expression.");
    /// <summary>Compares a password against a Scrypt hash. Maps to <c>crypto::scrypt::compare(pass, hash)</c>.</summary>
    public static bool ScryptCompare(string pass, string hash) => throw new NotSupportedException("SurrealCryptoFunctions.ScryptCompare can only be used inside a LINQ expression.");

    // ── General-purpose hashing ──

    /// <summary>Computes MD5 hash. Maps to <c>crypto::md5(data)</c>.</summary>
    public static string Md5(string data) => throw new NotSupportedException("SurrealCryptoFunctions.Md5 can only be used inside a LINQ expression.");
    /// <summary>Computes SHA-1 hash. Maps to <c>crypto::sha1(data)</c>.</summary>
    public static string Sha1(string data) => throw new NotSupportedException("SurrealCryptoFunctions.Sha1 can only be used inside a LINQ expression.");
    /// <summary>Computes SHA-256 hash. Maps to <c>crypto::sha256(data)</c>.</summary>
    public static string Sha256(string data) => throw new NotSupportedException("SurrealCryptoFunctions.Sha256 can only be used inside a LINQ expression.");
    /// <summary>Computes SHA-512 hash. Maps to <c>crypto::sha512(data)</c>.</summary>
    public static string Sha512(string data) => throw new NotSupportedException("SurrealCryptoFunctions.Sha512 can only be used inside a LINQ expression.");
}

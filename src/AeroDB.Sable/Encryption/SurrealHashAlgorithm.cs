namespace AeroDB.Sable;

/// <summary>SurrealDB one-way hashing algorithms exposed by Sable.</summary>
public enum SurrealHashAlgorithm
{
    Argon2 = 1,
    Bcrypt = 2,
    Pbkdf2 = 3,
    Scrypt = 4,
    Blake3 = 5,
    Joaat = 6,
    Md5 = 7,
    Sha1 = 8,
    Sha256 = 9,
    Sha512 = 10
}

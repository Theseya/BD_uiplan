using System.Security.Cryptography;
using System.Text;

namespace WebApplication1;

/// <summary>
/// Helpers for password hashing and verification. Used by login and user admin.
/// </summary>
public static class AuthHelpers
{
    public const int PasswordSaltSize = 16;
    public const int PasswordHashSize = 32;
    public const int PasswordIterations = 100000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(PasswordSaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            PasswordHashSize);
        var combined = new byte[PasswordSaltSize + PasswordHashSize];
        Buffer.BlockCopy(salt, 0, combined, 0, PasswordSaltSize);
        Buffer.BlockCopy(hash, 0, combined, PasswordSaltSize, PasswordHashSize);
        return Convert.ToBase64String(combined);
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            var combined = Convert.FromBase64String(storedHash);
            if (combined.Length != PasswordSaltSize + PasswordHashSize) return false;
            var salt = new byte[PasswordSaltSize];
            var storedHashBytes = new byte[PasswordHashSize];
            Buffer.BlockCopy(combined, 0, salt, 0, PasswordSaltSize);
            Buffer.BlockCopy(combined, PasswordSaltSize, storedHashBytes, 0, PasswordHashSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                PasswordIterations,
                HashAlgorithmName.SHA256,
                PasswordHashSize);
            return CryptographicOperations.FixedTimeEquals(hash, storedHashBytes);
        }
        catch
        {
            return false;
        }
    }
}

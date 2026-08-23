using System.Security.Cryptography;
using System.Text;

namespace PatientIntakeApp.Services;

public interface IPasswordHasher
{
    (string Hash, string Salt) HashPassword(string password);
    bool Verify(string password, string hashBase64, string saltBase64);
}

public class PasswordHasher : IPasswordHasher
{
    // Demo auth only. Upgrade to real auth later.
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int Iterations = 150_000;

    public (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(password, salt);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string password, string hashBase64, string saltBase64)
    {
        if (string.IsNullOrWhiteSpace(hashBase64) || string.IsNullOrWhiteSpace(saltBase64)) return false;
        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch
        {
            return false;
        }

        var actual = Pbkdf2(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string password, byte[] salt)
    {
        // Use UTF8 and PBKDF2-SHA256.
        var pwdBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        return Rfc2898DeriveBytes.Pbkdf2(pwdBytes, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
    }
}


using System.Text;
using System.Security.Cryptography;

namespace WebApi.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = null!;

    public string Audience { get; set; } = null!;

    public string Key { get; set; } = null!;

    public int LifetimeMinutes { get; set; }

    public void PrepareSigningKey(bool allowTemporaryKey)
    {
        // Use an in-memory random key on the first local launch
        // Production requires an explicitly configured secret
        if (allowTemporaryKey && string.IsNullOrWhiteSpace(Key))
            Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Validate();
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Encoding.UTF8.GetByteCount(Key) < 32)
            throw new InvalidOperationException(
                "Configure Jwt:Key using user secrets or Jwt__Key; it must contain at least 32 UTF-8 bytes.");
    }
}

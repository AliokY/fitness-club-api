using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DataAccessLayer.Entities;
using Microsoft.IdentityModel.Tokens;

namespace WebApi.Auth;

public class AccessTokenService
{
    private readonly JwtOptions _options;

    public AccessTokenService(JwtOptions options)
    {
        _options = options;
    }

    public LoginResponse Create(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Role, user.Role)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            DateTime.UtcNow,
            expiresAt,
            credentials);

        return new LoginResponse
        {
            AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = expiresAt
        };
    }
}

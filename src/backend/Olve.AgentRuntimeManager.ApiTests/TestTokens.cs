using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// Mints HS256 JWTs the target accepts: it must run with the same <c>Auth:SigningKey</c>,
/// <c>Auth:Authority</c> (issuer) and <c>Auth:Audience</c>.
/// </summary>
public sealed record TestTokens(string SigningKey, string Issuer, string Audience)
{
    public string Mint()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, "api-test-user")]),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

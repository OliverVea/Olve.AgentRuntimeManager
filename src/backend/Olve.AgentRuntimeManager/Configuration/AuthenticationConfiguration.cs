using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Olve.AgentRuntimeManager.Configuration;

public static class AuthenticationConfiguration
{
    public static void ConfigureAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var authority = builder.Configuration["Auth:Authority"];
                var audience = builder.Configuration["Auth:Audience"];
                var signingKey = builder.Configuration["Auth:SigningKey"];
                var frontendAuthority = builder.Configuration["Auth:Frontend:Authority"];
                var frontendClientId = builder.Configuration["Auth:Frontend:ClientId"];

                options.Authority = authority;

                // The SPA logs in via its own PUBLIC Authentik provider, so its tokens carry that
                // provider's `iss` and an `aud` of its own client id — both different from the
                // resource provider's. Trust both issuers and both audiences (the SPA is this API's
                // own frontend). Both providers share Authentik's signing key, so the Authority
                // JWKS validates either signature; only the issuer/audience claims need widening.
                var validIssuers = new[] { authority, frontendAuthority }
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct()
                    .ToArray();
                var validAudiences = new[] { audience, frontendClientId }
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct()
                    .ToArray();

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = validIssuers.Length > 0,
                    ValidIssuers = validIssuers.Length > 0 ? validIssuers : null,
                    ValidateAudience = validAudiences.Length > 0,
                    ValidAudiences = validAudiences.Length > 0 ? validAudiences : null,
                    ValidateLifetime = true,
                };

                if (builder.Environment.IsDevelopment())
                {
                    options.RequireHttpsMetadata = false;
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                }

                if (signingKey is not null)
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
                    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                }

                // Diagnostics for the SPA login: a rejected bearer is otherwise a silent 401.
                // Failures log at Warning (visible everywhere) with the IDX reason + the token's
                // iss/aud/kid so a mismatch against ValidIssuers/ValidAudiences is obvious; the
                // happy path logs at Debug (enable via Logging:LogLevel in beta). Never the token.
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Olve.AgentRuntimeManager.Auth");
                        var (iss, aud, kid) = DescribeToken(context.Request);
                        logger.LogWarning(context.Exception,
                            "JWT auth failed: {Reason} | token iss={Iss} aud={Aud} kid={Kid} | expected iss=[{ValidIssuers}] aud=[{ValidAudiences}]",
                            context.Exception.Message, iss, aud, kid,
                            string.Join(", ", validIssuers), string.Join(", ", validAudiences));
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Olve.AgentRuntimeManager.Auth");
                        logger.LogDebug(
                            "JWT challenge on {Path}: error={Error} desc={Desc}",
                            context.Request.Path, context.Error, context.ErrorDescription);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Olve.AgentRuntimeManager.Auth");
                        var principal = context.Principal;
                        logger.LogDebug("JWT validated: iss={Iss} aud={Aud} sub={Sub}",
                            principal?.FindFirst("iss")?.Value,
                            string.Join(",", principal?.FindAll("aud").Select(c => c.Value) ?? []),
                            principal?.FindFirst("sub")?.Value);
                        return Task.CompletedTask;
                    },
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(
                new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build());
    }

    public static void MapAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    // Read iss/aud/kid from the *incoming* bearer without validating it — so a rejected token's
    // claims are visible in the failure log next to what the API expected. Best-effort: any parse
    // problem yields nulls rather than throwing inside the auth event.
    private static (string? Iss, string? Aud, string? Kid) DescribeToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, null);
        }

        var parts = header["Bearer ".Length..].Trim().Split('.');
        if (parts.Length < 2)
        {
            return (null, null, null);
        }

        try
        {
            return (ReadClaim(parts[1], "iss"), ReadClaim(parts[1], "aud"), ReadClaim(parts[0], "kid"));
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? ReadClaim(string segment, string name)
    {
        using var doc = JsonDocument.Parse(Base64UrlDecode(segment));
        if (!doc.RootElement.TryGetProperty(name, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Array => string.Join(",", el.EnumerateArray().Select(x => x.GetString())),
            _ => el.ToString(),
        };
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}

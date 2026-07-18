using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Zumingtalk.Service.Security;

public sealed class AdminKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = configuration["Service:AdminApiKey"];
        var suppliedKey = Request.Headers["X-Admin-Key"].ToString();
        if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrWhiteSpace(suppliedKey) || !SecretHasher.FixedTimeEquals(configuredKey, suppliedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid administrator credential."));
        }

        var actor = Request.Headers["X-Admin-Actor"].ToString();
        if (string.IsNullOrWhiteSpace(actor))
        {
            actor = "administrator";
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, actor[..Math.Min(actor.Length, 128)])], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

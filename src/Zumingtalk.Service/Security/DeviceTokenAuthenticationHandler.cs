using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zumingtalk.Service.Data;

namespace Zumingtalk.Service.Security;

public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ServiceDbContext dbContext) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || authorization.Length == prefix.Length)
        {
            return AuthenticateResult.NoResult();
        }

        var tokenHash = SecretHasher.Hash(authorization[prefix.Length..]);
        var activation = await dbContext.DeviceActivations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.TokenHash == tokenHash && value.RevokedAt == null, Context.RequestAborted);
        if (activation is null)
        {
            return AuthenticateResult.Fail("Invalid device credential.");
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, activation.Id.ToString("D"))], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

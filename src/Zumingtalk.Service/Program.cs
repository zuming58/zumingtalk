using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Zumingtalk.Service.Commerce;
using Zumingtalk.Service.Data;
using Zumingtalk.Service.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ServiceDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Zumingtalk");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Zumingtalk must be configured through the environment or local secrets.");
    }

    options.UseNpgsql(connectionString);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ActivationService>();
builder.Services.AddScoped<AdminCommerceService>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>("Device", _ => { })
    .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthenticationHandler>("Admin", _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Device", policy =>
    {
        policy.AddAuthenticationSchemes("Device");
        policy.RequireAuthenticatedUser();
    });
    options.AddPolicy("Admin", policy =>
    {
        policy.AddAuthenticationSchemes("Admin");
        policy.RequireAuthenticatedUser();
    });
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/activation", async (ActivationRequest request, ActivationService service, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.InviteCode) || string.IsNullOrWhiteSpace(request.DeviceFingerprint) || request.InviteCode.Length > 128 || request.DeviceFingerprint.Length > 1024)
    {
        return Results.BadRequest(new { error = "Invalid activation request." });
    }

    var result = await service.ActivateAsync(request, cancellationToken);
    return result.Status switch
    {
        ActivationStatus.Success => Results.Ok(result.Response),
        ActivationStatus.InviteUnavailable => Results.Conflict(new { error = "Invitation code is unavailable." }),
        ActivationStatus.DeviceAlreadyActivated => Results.Conflict(new { error = "This device is already activated." }),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
});

app.MapGet("/api/me/entitlement", async (ClaimsPrincipal principal, EntitlementService service, CancellationToken cancellationToken) =>
{
    var activationId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return Results.Ok(await service.GetAsync(activationId, cancellationToken));
}).RequireAuthorization("Device");

var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
admin.MapPost("/invites", async (ClaimsPrincipal principal, AdminCommerceService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CreateInviteAsync(principal.Identity?.Name ?? "administrator", cancellationToken)));
admin.MapPost("/devices/{activationId:guid}/reset", async (Guid activationId, ClaimsPrincipal principal, AdminCommerceService service, CancellationToken cancellationToken) =>
    await service.ResetDeviceAsync(activationId, principal.Identity?.Name ?? "administrator", cancellationToken)
        ? Results.NoContent()
        : Results.NotFound());
admin.MapPost("/devices/{activationId:guid}/pro", async (Guid activationId, ClaimsPrincipal principal, AdminCommerceService service, CancellationToken cancellationToken) =>
    await service.GrantProAsync(activationId, principal.Identity?.Name ?? "administrator", cancellationToken)
        ? Results.NoContent()
        : Results.NotFound());
admin.MapPost("/devices/{activationId:guid}/add-on", async (Guid activationId, ClaimsPrincipal principal, AdminCommerceService service, CancellationToken cancellationToken) =>
    await service.GrantAddOnAsync(activationId, principal.Identity?.Name ?? "administrator", cancellationToken)
        ? Results.NoContent()
        : Results.NotFound());

app.Run();

public partial class Program;

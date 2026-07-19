using System.Security.Claims;
using System.Net.WebSockets;
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
builder.Services.AddScoped<QuotaSessionService>();
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
app.UseWebSockets();

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

app.MapPost("/api/asr/sessions", async (HttpContext context, ClaimsPrincipal principal, QuotaSessionService service, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Bailian:ApiKey"]))
    {
        return Results.Problem("Zumingtalk cloud recognition is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var activationId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var webSocketScheme = context.Request.IsHttps ? "wss" : "ws";
    var streamUrl = $"{webSocketScheme}://{context.Request.Host}/api/asr/stream";
    try
    {
        return Results.Ok(await service.ReserveAsync(activationId, streamUrl, cancellationToken));
    }
    catch (QuotaUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status402PaymentRequired);
    }
}).RequireAuthorization("Device");

app.MapPost("/api/asr/sessions/{sessionId:guid}/finish", async (Guid sessionId, AsrFinishRequest request, ClaimsPrincipal principal, QuotaSessionService service, CancellationToken cancellationToken) =>
{
    var activationId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    try
    {
        return Results.Ok(await service.FinishAsync(sessionId, activationId, request.Succeeded, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
}).RequireAuthorization("Device");

app.Map("/api/asr/stream", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!Guid.TryParse(context.Request.Query["sessionId"], out var sessionId) ||
        !TryGetBearerToken(context.Request.Headers.Authorization.ToString(), out var token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
    var activation = await db.DeviceActivations.AsNoTracking().SingleOrDefaultAsync(value => value.TokenHash == SecretHasher.Hash(token) && value.RevokedAt == null, context.RequestAborted);
    if (activation is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var quota = scope.ServiceProvider.GetRequiredService<QuotaSessionService>();
    var session = await db.AsrSessions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sessionId && value.ActivationId == activation.Id && (value.Status == AsrSessionStatus.Reserved || value.Status == AsrSessionStatus.Streaming), context.RequestAborted);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var bailianApiKey = builder.Configuration["Bailian:ApiKey"]!;
    var bailianEndpoint = builder.Configuration["Bailian:Endpoint"] ?? "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
    using var clientSocket = await context.WebSockets.AcceptWebSocketAsync();
    using var bailianSocket = new ClientWebSocket();
    bailianSocket.Options.SetRequestHeader("Authorization", $"Bearer {bailianApiKey}");
    bailianSocket.Options.SetRequestHeader("User-Agent", "Zumingtalk.Service/0.7");
    await bailianSocket.ConnectAsync(new Uri(bailianEndpoint), context.RequestAborted);

    using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var downstream = RelayBailianEventsAsync(bailianSocket, clientSocket, relayCts.Token);
    try
    {
        await RelayClientAudioAsync(clientSocket, bailianSocket, quota, sessionId, activation.Id, relayCts.Token);
    }
    finally
    {
        relayCts.Cancel();
        bailianSocket.Abort();
        try { await downstream; } catch (OperationCanceledException) { }
    }
});

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

static async Task RelayClientAudioAsync(WebSocket clientSocket, ClientWebSocket bailianSocket, QuotaSessionService quota, Guid sessionId, Guid activationId, CancellationToken cancellationToken)
{
    var buffer = new byte[16 * 1024];
    while (clientSocket.State == WebSocketState.Open && bailianSocket.State == WebSocketState.Open)
    {
        var result = await clientSocket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return;
        }

        if (!result.EndOfMessage)
        {
            throw new InvalidOperationException("Fragmented client WebSocket messages are not supported.");
        }

        if (result.MessageType == WebSocketMessageType.Binary)
        {
            await quota.RecordPcmAsync(sessionId, activationId, result.Count, cancellationToken);
        }

        await bailianSocket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, true, cancellationToken);
    }
}

static async Task RelayBailianEventsAsync(ClientWebSocket bailianSocket, WebSocket clientSocket, CancellationToken cancellationToken)
{
    var buffer = new byte[16 * 1024];
    while (bailianSocket.State == WebSocketState.Open && clientSocket.State == WebSocketState.Open)
    {
        var result = await bailianSocket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return;
        }

        await clientSocket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
    }
}

static bool TryGetBearerToken(string authorization, out string token)
{
    const string prefix = "Bearer ";
    if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && authorization.Length > prefix.Length)
    {
        token = authorization[prefix.Length..];
        return true;
    }

    token = string.Empty;
    return false;
}

public partial class Program;

public sealed record AsrFinishRequest(bool Succeeded);

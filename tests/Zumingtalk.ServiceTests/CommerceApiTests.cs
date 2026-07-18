using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Zumingtalk.Service.Commerce;
using Zumingtalk.Service.Data;
using Zumingtalk.Service.Security;

namespace Zumingtalk.ServiceTests;

public sealed class CommerceApiTests(CommerceApiFactory factory) : IClassFixture<CommerceApiFactory>
{
    [Fact]
    public async Task InvitationCanActivateOnlyOneDeviceAndNeverPersistsPlainCredentials()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-a");

        var repeat = await factory.CreateClient().PostAsJsonAsync("/api/activation", new ActivationRequest(inviteCode, "device-fingerprint-b"));
        var entitlement = await AuthenticatedGetAsync(activation.DeviceToken, "/api/me/entitlement");

        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.OK, entitlement.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var storedInvite = await db.InviteCodes.SingleAsync();
        var storedActivation = await db.DeviceActivations.SingleAsync();
        Assert.NotEqual(inviteCode, storedInvite.CodeHash);
        Assert.NotEqual(activation.DeviceToken, storedActivation.TokenHash);
        Assert.Equal(64, storedInvite.CodeHash.Length);
        Assert.Equal(64, storedActivation.TokenHash.Length);
        Assert.Equal(600, await db.QuotaBuckets.Where(value => value.Kind == QuotaBucketKind.Trial).Select(value => value.RemainingSeconds).SingleAsync());
    }

    [Fact]
    public async Task DeviceResetRevokesTokenAndAllowsTheInvitationToBeRebound()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-reset-a");
        var activationId = await GetActivationIdAsync(activation.DeviceToken);

        var reset = await AdminPostAsync($"/api/admin/devices/{activationId:D}/reset");
        var oldToken = await AuthenticatedGetAsync(activation.DeviceToken, "/api/me/entitlement");
        var rebound = await ActivateAsync(inviteCode, "device-fingerprint-reset-b");

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        Assert.NotEmpty(rebound.DeviceToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var audit = await db.AdminAuditLogs.SingleAsync(value => value.Action == "device.reset");
        Assert.DoesNotContain(activation.DeviceToken, audit.Metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(inviteCode, audit.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorGrantsFixedProAndAddOnQuotasWithNonSensitiveAuditEntries()
    {
        var inviteCode = await CreateInviteAsync();
        var activation = await ActivateAsync(inviteCode, "device-fingerprint-grant");
        var activationId = await GetActivationIdAsync(activation.DeviceToken);

        var pro = await AdminPostAsync($"/api/admin/devices/{activationId:D}/pro");
        var addOn = await AdminPostAsync($"/api/admin/devices/{activationId:D}/add-on");

        Assert.Equal(HttpStatusCode.NoContent, pro.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, addOn.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var buckets = await db.QuotaBuckets.OrderBy(value => value.Kind).ToListAsync();
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.ProMonthly && value.RemainingSeconds == 36_000 && value.ExpiresAt is not null);
        Assert.Contains(buckets, value => value.Kind == QuotaBucketKind.AddOn && value.RemainingSeconds == 36_000 && value.ExpiresAt is null);
        var auditEntries = await db.AdminAuditLogs.OrderBy(value => value.Action).ToListAsync();
        Assert.Contains(auditEntries, value => value.Action == "entitlement.grant_pro" && value.Metadata == "{\"days\":30,\"seconds\":36000}");
        Assert.Contains(auditEntries, value => value.Action == "quota.grant_add_on" && value.Metadata == "{\"seconds\":36000}");
        Assert.DoesNotContain(auditEntries, value => value.Metadata.Contains(inviteCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdministratorEndpointsRejectRequestsWithoutServerSideCredential()
    {
        var response = await factory.CreateClient().PostAsync("/api/admin/invites", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> CreateInviteAsync()
    {
        var response = await AdminPostAsync("/api/admin/invites");
        response.EnsureSuccessStatusCode();
        var invite = await response.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOptions);
        return Assert.IsType<CreateInviteResponse>(invite).InviteCode;
    }

    private async Task<ActivationResponse> ActivateAsync(string inviteCode, string fingerprint)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/activation", new ActivationRequest(inviteCode, fingerprint));
        response.EnsureSuccessStatusCode();
        return Assert.IsType<ActivationResponse>(await response.Content.ReadFromJsonAsync<ActivationResponse>(JsonOptions));
    }

    private async Task<Guid> GetActivationIdAsync(string deviceToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var tokenHash = SecretHasher.Hash(deviceToken);
        return await db.DeviceActivations.Where(value => value.TokenHash == tokenHash).Select(value => value.Id).SingleAsync();
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Admin-Key", CommerceApiFactory.AdminKey);
        request.Headers.Add("X-Admin-Actor", "test-administrator");
        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string deviceToken, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
        return await factory.CreateClient().SendAsync(request);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed class CommerceApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "test-admin-key-not-a-production-secret";
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Service:AdminApiKey"] = AdminKey,
                ["ConnectionStrings:Zumingtalk"] = "Host=not-used-by-tests"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ServiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ServiceDbContext>>();
            services.AddDbContext<ServiceDbContext>(options => options.UseSqlite(connection));
        });
    }

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await connection.DisposeAsync();
        Dispose();
    }
}

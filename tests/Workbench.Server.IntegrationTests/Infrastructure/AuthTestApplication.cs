// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class AuthTestApplication : IAsyncDisposable
{
    public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DisabledUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Correct Horse Battery Staple 1!";
    public const string DisabledEmail = "disabled@example.com";
    private readonly SqlTestDatabase _database;

    private AuthTestApplication(SqlTestDatabase database, WebApplicationFactory<Program> factory)
    {
        _database = database;
        Factory = factory;
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string AdminConnectionString => _database.AdminConnectionString;

    public static async Task<AuthTestApplication> CreateAsync(SqlServerFixture sqlServer)
    {
        var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var webConnection = await database.CreateWebUserAsync();
        await SeedAsync(database.AdminConnectionString);
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Workbench", webConnection));
        return new AuthTestApplication(database, factory);
    }

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    private static async Task SeedAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var database = new WorkbenchDbContext(options, TenantContext.None);
        var now = DateTimeOffset.UtcNow;
        var passwordHasher = new PasswordHasher<WorkbenchUser>();
        var admin = CreateUser(AdminUserId, AdminEmail, now);
        admin.PasswordHash = passwordHasher.HashPassword(admin, AdminPassword);
        var disabled = CreateUser(DisabledUserId, DisabledEmail, now);
        disabled.State = AccountState.Disabled;
        disabled.PasswordHash = passwordHasher.HashPassword(disabled, AdminPassword);
        var roleId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        database.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Tenant A",
            NormalizedName = "TENANT A",
            CreatedAtUtc = now,
        });
        database.Users.AddRange(admin, disabled);
        database.LoginDirectory.AddRange(
            new LoginDirectoryEntry
            {
                NormalizedEmail = AdminEmail.ToUpperInvariant(),
                UserId = admin.Id,
                TenantId = TenantId,
            },
            new LoginDirectoryEntry
            {
                NormalizedEmail = DisabledEmail.ToUpperInvariant(),
                UserId = disabled.Id,
                TenantId = TenantId,
            });
        database.Roles.Add(new WorkbenchRole
        {
            Id = roleId,
            TenantId = TenantId,
            Name = "Tenant administrator",
            NormalizedName = "TENANT ADMINISTRATOR",
        });
        database.Set<WorkbenchUserRole>().Add(new WorkbenchUserRole
        {
            TenantId = TenantId,
            UserId = admin.Id,
            RoleId = roleId,
        });
        database.Set<WorkbenchRoleClaim>().AddRange(
            new WorkbenchRoleClaim
            {
                TenantId = TenantId,
                RoleId = roleId,
                ClaimType = SessionCookieHandler.PermissionClaimType,
                ClaimValue = WorkbenchPermissions.TenantAccess,
            },
            new WorkbenchRoleClaim
            {
                TenantId = TenantId,
                RoleId = roleId,
                ClaimType = SessionCookieHandler.PermissionClaimType,
                ClaimValue = WorkbenchPermissions.TenantUsersManage,
            });
        await database.SaveChangesAsync();
    }

    private static WorkbenchUser CreateUser(Guid id, string email, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = TenantId,
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = now,
    };
}

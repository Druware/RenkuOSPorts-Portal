using System.Text;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RenkuPorts.Server.Data;
using RenkuPorts.Server.Endpoints;
using RenkuPorts.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Retries transient Azure SQL errors. No code opens its own transaction, which this execution strategy would reject.
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cfg.GetConnectionString("Sql"), sql => sql.EnableRetryOnFailure()));
builder.Services.AddIdentity<AppUser, IdentityRole>(o => o.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "renkuports";
    o.Cookie.SameSite = SameSiteMode.Lax; // blocks cross-site POSTs carrying the cookie (CSRF)
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

var auth = builder.Services.AddAuthentication().AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = ClientTokens.ValidationParameters(cfg);
});
if (!string.IsNullOrEmpty(cfg["Auth:GitHub:ClientId"]))
{
    auth.AddGitHub(o =>
    {
        o.ClientId = cfg["Auth:GitHub:ClientId"]!;
        o.ClientSecret = cfg["Auth:GitHub:ClientSecret"] ?? "";
        o.Scope.Add("user:email");
        o.SignInScheme = IdentityConstants.ExternalScheme;
    });
}
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"))
    .AddPolicy("BuildClient", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireClaim("sub"));

// Blob:ServiceUri (managed identity) wins over the connection string.
builder.Services.AddSingleton(_ => (cfg["Blob:ServiceUri"] is { Length: > 0 } blobUri
        ? new BlobServiceClient(new Uri(blobUri), new DefaultAzureCredential())
        : new BlobServiceClient(cfg.GetConnectionString("Blob")))
    .GetBlobContainerClient(cfg["Blob:Container"] ?? "packages"));
// Keys in blob storage, so restarts, deploys and other instances keep accepting the sign-in cookie.
builder.Services.AddDataProtection().SetApplicationName("RenkuPorts")
    .PersistKeysToAzureBlobStorage(sp => sp.GetRequiredService<BlobContainerClient>().GetBlobClient("dataprotection/keys.xml"));
if (!string.IsNullOrEmpty(cfg["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
builder.Services.AddSingleton<INotifier>(sp => cfg["Email:Mode"] == "AzureCommunication"
    ? new AzureEmailNotifier(cfg)
    : new SmtpNotifier(cfg));

// Room for a 5 MB build log sent as a form field to /complete. Package uploads do not use form binding.
builder.Services.Configure<FormOptions>(o => o.ValueLengthLimit = 16 << 20);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

var app = builder.Build();

await Startup.InitializeAsync(app.Services);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

PublicEndpoints.Map(app);
AuthEndpoints.Map(app);
ClientsEndpoints.Map(app);
AdminEndpoints.Map(app);
BuildClientApi.Map(app);

app.MapFallback("/api/{**rest}", () => Results.NotFound());
// /repo/{platform} is an SPA route; only the package download path under /repo is the server's.
app.MapFallback("/repo/{platform}/packages/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Startup work: apply migrations (unless Database:MigrateOnStartup is false), create the blob container, seed roles and the admin account.</summary>
static class Startup
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var cfg = sp.GetRequiredService<IConfiguration>();
        if (cfg.GetValue("Database:MigrateOnStartup", true)) await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await sp.GetRequiredService<BlobContainerClient>().CreateIfNotExistsAsync();

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var r in new[] { "Admin", "Builder" })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        var email = cfg["Seed:AdminEmail"];
        if (string.IsNullOrEmpty(email)) return;
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
            var res = await users.CreateAsync(admin, cfg["Seed:AdminPassword"] ?? throw new InvalidOperationException("Seed:AdminPassword is not set"));
            if (!res.Succeeded) throw new InvalidOperationException("Seeding admin failed: " + string.Join("; ", res.Errors.Select(e => e.Description)));
        }
        if (!await users.IsInRoleAsync(admin, "Admin")) await users.AddToRoleAsync(admin, "Admin");
    }
}

/// <summary>Issues and validates the JWTs build clients use (HMAC-SHA256 with <c>Jwt:Key</c>, 12 h).</summary>
public static class ClientTokens
{
    public const string Issuer = "renkuports";
    public const string Audience = "renkuports-client";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static SymmetricSecurityKey Key(IConfiguration cfg)
    {
        var key = cfg["Jwt:Key"];
        if (key is null || key.Length < 32) throw new InvalidOperationException("Jwt:Key must be set to at least 32 characters");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public static TokenValidationParameters ValidationParameters(IConfiguration cfg) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key(cfg),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
}

public partial class Program;

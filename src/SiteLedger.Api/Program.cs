using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SiteLedger.Api.Auth;
using SiteLedger.Api.Projects;
using SiteLedger.Core;
using SiteLedger.Infrastructure;
using TokenOptions = SiteLedger.Api.Auth.TokenOptions;

var builder = WebApplication.CreateBuilder(args);
var tokenSection = builder.Configuration.GetSection(TokenOptions.SectionName);
var tokenSettings = tokenSection.Get<TokenOptions>() ?? new TokenOptions();
var database = builder.Configuration.GetConnectionString("SiteLedger") ?? throw new InvalidOperationException("ConnectionStrings:SiteLedger is required.");
if (string.IsNullOrWhiteSpace(tokenSettings.Issuer) || string.IsNullOrWhiteSpace(tokenSettings.Audience) || Encoding.UTF8.GetByteCount(tokenSettings.SigningKey) < 32 || tokenSettings.AccessTokenMinutes is < 1 or > 60)
    throw new InvalidOperationException("JWT issuer, audience, signing key of at least 32 bytes, and a 1-60 minute lifetime are required.");

builder.Services.Configure<TokenOptions>(tokenSection);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, CurrentTenant>();
builder.Services.AddDbContext<SiteLedgerDbContext>(options => options.UseMySql(database, new MySqlServerVersion(new Version(8, 0, 36))));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;
}).AddEntityFrameworkStores<SiteLedgerDbContext>().AddDefaultTokenProviders();
builder.Services.AddScoped<ILoginService, LoginService>();
builder.Services.AddScoped<AuthenticatorService>();
builder.Services.AddScoped<IAccountEmailSender, SmtpAccountEmailSender>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = tokenSettings.Issuer,
        ValidateAudience = true, ValidAudience = tokenSettings.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenSettings.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var principal = context.Principal;
            var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var tenantClaim = principal?.FindFirstValue("tenant_id");
            var manager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = id is null ? null : await manager.FindByIdAsync(id);
            var db = context.HttpContext.RequestServices.GetRequiredService<SiteLedgerDbContext>();
            if (user is null || !user.IsActive || !user.EmailConfirmed || user.TenantId is null ||
                !Guid.TryParse(tenantClaim, out var tenantId) || user.TenantId != tenantId ||
                user.SecurityStamp is null || principal?.FindFirstValue("security_stamp") != user.SecurityStamp ||
                !await db.Tenants.AnyAsync(x => x.Id == tenantId && x.SubscriptionStatus == SubscriptionStatus.Active, context.HttpContext.RequestAborted))
            {
                context.Fail("Account or tenant is unavailable.");
                return;
            }
            var roles = await manager.GetRolesAsync(user);
            var tokenRoles = principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
            if (!tokenRoles.SetEquals(roles)) context.Fail("Account roles have changed.");
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Permissions.All)
        options.AddPolicy(permission, policy => policy.RequireRole(Permissions.For(permission)));
});
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program;

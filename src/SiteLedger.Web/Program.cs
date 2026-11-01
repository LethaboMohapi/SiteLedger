using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var keyPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keyPath) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("DataProtection:KeysPath is required outside development.");
if (!string.IsNullOrWhiteSpace(keyPath))
{
    if (!Path.IsPathFullyQualified(keyPath)) throw new InvalidOperationException("DataProtection:KeysPath must be absolute.");
    var protection = builder.Services.AddDataProtection().SetApplicationName("SiteLedger.Web").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
    if (!string.IsNullOrWhiteSpace(certificatePath))
        protection.ProtectKeysWithCertificate(new X509Certificate2(certificatePath, builder.Configuration["DataProtection:CertificatePassword"]));
    else if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("DataProtection:CertificatePath is required when persisting keys outside development.");
}

var apiBase = builder.Configuration["SiteLedgerApi:BaseUrl"] ?? "http://localhost:5051/";
if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var apiUri) ||
    (apiUri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && apiUri.Scheme == Uri.UriSchemeHttp && (apiUri.IsLoopback || builder.Configuration.GetValue<bool>("SiteLedgerApi:AllowHttpDevelopment")))))
    throw new InvalidOperationException("SiteLedgerApi:BaseUrl must be HTTPS, except for loopback in development.");
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis)) builder.Services.AddStackExchangeRedisCache(options => { options.Configuration = redis; options.InstanceName = "SiteLedger.Web:"; });
else if (builder.Environment.IsDevelopment()) builder.Services.AddDistributedMemoryCache();
else throw new InvalidOperationException("ConnectionStrings:Redis is required outside development.");
builder.Services.AddHttpClient<ApiClient>(client => { client.BaseAddress = apiUri; client.Timeout = TimeSpan.FromSeconds(30); });
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SessionCoordinator>();
builder.Services.AddSingleton<WebSessions>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.SlidingExpiration = false;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 106954752);
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews(options => { options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); options.Filters.Add<ValidateForm>(); });
var app = builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Home/Error"); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

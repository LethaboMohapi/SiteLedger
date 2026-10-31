using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var apiBase = builder.Configuration["SiteLedgerApi:BaseUrl"] ?? "http://localhost:5051/";
if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var apiUri) ||
    (apiUri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && apiUri.IsLoopback && apiUri.Scheme == Uri.UriSchemeHttp)))
    throw new InvalidOperationException("SiteLedgerApi:BaseUrl must be HTTPS, except for loopback in development.");
builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpClient<ApiClient>(client => { client.BaseAddress = apiUri; client.Timeout = TimeSpan.FromSeconds(30); });
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.SlidingExpiration = false;
});
builder.Services.AddAuthorization();
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
var app = builder.Build();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Home/Error"); app.UseHsts(); }
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

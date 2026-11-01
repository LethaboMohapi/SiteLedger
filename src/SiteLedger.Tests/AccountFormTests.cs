using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SiteLedger.Web.Services;

namespace SiteLedger.Tests;

public sealed class AccountFormTests
{
    private sealed class Transport : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.BadRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"errors\":[\"Passwords require a symbol.\"]}", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static WebApplicationFactory<SiteLedger.Web.WebEntryPoint> Factory(Transport transport) =>
        new WebApplicationFactory<SiteLedger.Web.WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddHttpClient<ApiClient>().ConfigurePrimaryHttpMessageHandler(() => transport);
            });
        });

    private static HttpClient Client(WebApplicationFactory<SiteLedger.Web.WebEntryPoint> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> Post(HttpClient client, string page, string action, Dictionary<string, string> values)
    {
        var html = await client.GetStringAsync(page);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(match.Success);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        return await client.PostAsync(action, new FormUrlEncodedContent(values));
    }

    [Fact]
    public async Task InvalidRegistrationKeepsSafeFieldsAndClearsSecretsWithoutCallingApi()
    {
        var transport = new Transport(); await using var factory = Factory(transport); using var client = Client(factory);
        var response = await Post(client, "/Account/Register", "/Account/Register", new()
        {
            ["FullName"] = "ExamplePerson", ["CompanyName"] = "ExampleCompany", ["Email"] = "person@example.test",
            ["Password"] = "Private-test-password-123!", ["ConfirmPassword"] = "A-different-secret-123!"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The passwords do not match.", html);
        Assert.Contains("value=\"ExamplePerson\"", html); Assert.Contains("value=\"ExampleCompany\"", html);
        Assert.DoesNotContain("Private-test-password", html); Assert.DoesNotContain("A-different-secret", html);
        Assert.True(response.Headers.CacheControl?.NoStore); Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RegistrationExplainsApiPasswordValidationAndProvidesClientValidation()
    {
        var transport = new Transport(); await using var factory = Factory(transport); using var client = Client(factory);
        var response = await Post(client, "/Account/Register", "/Account/Register", new()
        {
            ["FullName"] = "Example", ["CompanyName"] = "Company", ["Email"] = "person@example.test",
            ["Password"] = "Policy-invalid", ["ConfirmPassword"] = "Policy-invalid"
        });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Passwords require a symbol.", html); Assert.DoesNotContain("Policy-invalid", html);
        Assert.Contains("data-val-equalto", html);
        Assert.True(html.IndexOf("/lib/jquery/dist/jquery.min.js", StringComparison.Ordinal) < html.IndexOf("/lib/jquery-validation/dist", StringComparison.Ordinal));
        Assert.Single(transport.Requests);
        Assert.Equal("/api/v1/account/register", transport.Requests[0].Path);
        Assert.DoesNotContain("confirmPassword", transport.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidLoginEmailAndAuthenticatorAreRejectedWithoutLosingEmailOrEchoingSecrets()
    {
        var transport = new Transport(); await using var factory = Factory(transport); using var client = Client(factory);
        var response = await Post(client, "/Account/Login", "/Account/Login", new()
        {
            ["Email"] = "invalid-address", ["Password"] = "Secret-login-password!", ["AuthenticatorCode"] = "12", ["RecoveryCode"] = "Private-recovery-code"
        });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(transport.Requests);
        Assert.Contains("value=\"invalid-address\"", html); Assert.Contains("six digits", html);
        Assert.DoesNotContain("Secret-login-password", html); Assert.DoesNotContain("Private-recovery-code", html);
    }

    [Fact]
    public async Task RecoveryValidatesActionAndRoutesConfirmationWithoutBindingResetForm()
    {
        var transport = new Transport { Status = HttpStatusCode.Accepted }; await using var factory = Factory(transport); using var client = Client(factory);
        var invalid = await Post(client, "/Account/Recovery", "/Account/Recovery", new()
        {
            ["Request.Email"] = "person@example.test", ["Request.ActionType"] = "unrecognised"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Empty(transport.Requests);
        var accepted = await Post(client, "/Account/Recovery", "/Account/Recovery", new()
        {
            ["Request.Email"] = "person@example.test", ["Request.ActionType"] = "confirm"
        });
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Single(transport.Requests); Assert.Equal("/api/v1/account/confirmation/resend", transport.Requests[0].Path);
        Assert.Equal("person@example.test", JsonDocument.Parse(transport.Requests[0].Body).RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task ResetChecksPasswordConfirmationThenClearsSecretsOnApiFailureAndRedirectsOnSuccess()
    {
        var transport = new Transport(); await using var factory = Factory(transport); using var client = Client(factory);
        Dictionary<string, string> Values(string confirmation) => new()
        {
            ["Reset.Email"] = "person@example.test", ["Reset.Token"] = "Private-reset-token",
            ["Reset.Password"] = "New-private-password-123!", ["Reset.ConfirmPassword"] = confirmation
        };
        var mismatch = await Post(client, "/Account/Recovery", "/Account/Reset", Values("Different-password-123!"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode); Assert.Empty(transport.Requests);
        var rejected = await Post(client, "/Account/Recovery", "/Account/Reset", Values("New-private-password-123!"));
        var html = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("value=\"person@example.test\"", html);
        Assert.DoesNotContain("Private-reset-token", html); Assert.DoesNotContain("New-private-password", html);
        Assert.Single(transport.Requests);
        transport.Status = HttpStatusCode.OK;
        var success = await Post(client, "/Account/Recovery", "/Account/Reset", Values("New-private-password-123!"));
        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode); Assert.Equal("/Account/Login", success.Headers.Location?.OriginalString);
        var csrf = await client.PostAsync("/Account/Reset", new FormUrlEncodedContent(Values("New-private-password-123!")));
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode); Assert.Equal(2, transport.Requests.Count);
    }
}

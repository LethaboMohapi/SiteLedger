using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace SiteLedger.Web.Services;

public sealed class ApiClient(HttpClient http, IHttpContextAccessor accessor, WebSessions sessions)
{
    private async Task<string?> Access(string key)
    {
        try { return await sessions.Access(key, http, accessor.HttpContext?.RequestAborted ?? default); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or StackExchange.Redis.RedisException) { return null; }
    }
    public async Task<bool> Revoke(string key)
    {
        try { return await sessions.Revoke(key, http, accessor.HttpContext?.RequestAborted ?? default); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or StackExchange.Redis.RedisException) { return false; }
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (authenticated)
        {
            var key = accessor.HttpContext?.User.FindFirstValue("token_key");
            var token = key is null ? null : await Access(key);
            if (token is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        try { return await http.SendAsync(request); }
        catch (HttpRequestException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
        catch (TaskCanceledException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
    }

    public async Task<HttpResponseMessage> Identity(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try { return await http.SendAsync(request); }
        catch (HttpRequestException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
        catch (TaskCanceledException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
    }

    public async Task<HttpResponseMessage> Upload(string path, IFormFile file, int category)
    {
        var key = accessor.HttpContext?.User.FindFirstValue("token_key");
        var token = key is null ? null : await Access(key);
        if (token is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(file.OpenReadStream()), "file", Path.GetFileName(file.FileName));
        form.Add(new StringContent(category.ToString(System.Globalization.CultureInfo.InvariantCulture)), "category");
        request.Content = form;
        try { return await http.SendAsync(request); }
        catch (HttpRequestException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
        catch (TaskCanceledException) { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
    }

    public static async Task<JsonElement[]> Array(HttpResponseMessage response) => response.IsSuccessStatusCode
        ? await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? [] : [];
}

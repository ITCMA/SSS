using System.Net.Http.Headers;
using System.Text.Json;
using Sss.Web.Providers;

namespace Sss.Web.Citrix;

/// <summary>Settings for Citrix Cloud (DaaS) instead of an on-premises site (section "Sss:Citrix:Cloud"). Read-only: the data comes from the
/// Monitor service in the cloud, so sessions can be seen but not logged off. The secret should come from the environment
/// (<c>Sss__Citrix__Cloud__ClientSecret</c>), not from a file in the repository.</summary>
public sealed class CloudOptions
{
    /// <summary>Use Citrix Cloud (DaaS) instead of Delivery Controllers: read-only, no snap-in and no VDA access.</summary>
    public bool Enabled { get; set; }

    /// <summary>"eu" (api-eu.cloud.com), "us" (api.cloud.com), "ap-s" (api-ap-s.cloud.com) or "jp" (api.citrixcloud.jp).</summary>
    public string Region { get; set; } = "eu";

    /// <summary>Customer id of the Citrix Cloud account (shown with the API clients).</summary>
    public string CustomerId { get; set; } = "";

    /// <summary>Id of a Citrix Cloud API client (Identity and Access Management > API access). Give it the least rights that allow reading the Monitor data.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Secret of that API client.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Overrides the address derived from the region (testing); an https:// address without a trailing path.</summary>
    public string? ApiBase { get; set; }

    public static string HostFor(string region) => region.ToLowerInvariant() switch
    {
        "us" => "api.cloud.com",
        "ap-s" => "api-ap-s.cloud.com",
        "jp" => "api.citrixcloud.jp",
        _ => "api-eu.cloud.com",
    };

    public Uri Api => new((ApiBase ?? $"https://{HostFor(Region)}").TrimEnd('/') + "/");
    public Uri MonitorBase => new(Api, "monitorodata/");

    /// <summary>Why the settings cannot be used, or null.</summary>
    public string? Problem() =>
        !Enabled ? null
        : CustomerId.Length == 0 || ClientId.Length == 0 || ClientSecret.Length == 0 ? "Sss:Citrix:Cloud needs CustomerId, ClientId and ClientSecret."
        : !Uri.TryCreate(ApiBase ?? $"https://{HostFor(Region)}", UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ? "Sss:Citrix:Cloud:ApiBase must be an https:// address."
        : !CustomerId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? "Sss:Citrix:Cloud:CustomerId contains invalid characters."
        : null;
}

/// <summary>Signs every request to the Monitor service in Citrix Cloud with a bearer token of the API client (client credentials), fetched when needed
/// and kept until shortly before it expires.</summary>
public sealed class CloudAuthHandler(CloudOptions cloud, HttpMessageHandler inner, TimeProvider clock) : DelegatingHandler(inner)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _until;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _until) return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _until) return _token;
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(cloud.Api, $"cctrustoauth2/{cloud.CustomerId}/tokens/clients"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = cloud.ClientId, ["client_secret"] = cloud.ClientSecret,
                }),
            };
            using var resp = await base.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new ProviderUnavailableException($"Citrix Cloud sign-in failed ({(int)resp.StatusCode}): check CustomerId, ClientId, ClientSecret and Region.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token)) throw new ProviderUnavailableException("Citrix Cloud sign-in returned no token.");
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s1) ? s1
                : doc.RootElement.TryGetProperty("expires_in", out var e2) && e2.ValueKind == JsonValueKind.Number && e2.TryGetInt32(out var s2) ? s2 : 3600;
            _until = clock.GetUtcNow().AddSeconds(Math.Max(30, seconds - 120));
            return _token = token;
        }
        finally { _gate.Release(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.Equals(request.RequestUri?.Host, cloud.Api.Host, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Citrix Cloud token is only sent to the Citrix Cloud API.");
        request.Headers.Authorization = new AuthenticationHeaderValue("CWSAuth", "bearer=" + await TokenAsync(ct));
        request.Headers.TryAddWithoutValidation("Citrix-CustomerId", cloud.CustomerId);
        var resp = await base.SendAsync(request, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null;   // expired early: a new one is fetched with the next request
        // The cloud limits the request rate: one polite retry after the time it names (at most 5 s) for a read.
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && request.Method == HttpMethod.Get)
        {
            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            resp.Dispose();
            await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, ct);
            using var again = new HttpRequestMessage(request.Method, request.RequestUri);
            again.Headers.Authorization = request.Headers.Authorization;
            again.Headers.TryAddWithoutValidation("Citrix-CustomerId", cloud.CustomerId);
            foreach (var h in request.Headers.Where(h => !again.Headers.Contains(h.Key))) again.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await base.SendAsync(again, ct);
        }
        return resp;
    }
}

/// <summary>Raised for actions that need the on-premises Broker (log off, message, processes) when SSS runs against Citrix Cloud.</summary>
public sealed class ActionNotSupportedException(string message) : Exception(message);

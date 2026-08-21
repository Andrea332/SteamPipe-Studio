using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPipeStudio.Core.Steam;

public sealed record SteamBuild(
    uint BuildId,
    string Description,
    DateTimeOffset? CreatedUtc,
    string? AccountName,
    IReadOnlyList<string> LiveBranches);

public sealed record SteamBranch(
    string Name,
    uint BuildId,
    string Description,
    DateTimeOffset? TimeUpdatedUtc,
    bool PasswordRequired);

public sealed class PartnerApiException : Exception
{
    public PartnerApiException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => Status = status;

    public HttpStatusCode? Status { get; }
}

/// <summary>
/// Read/write access to build history and branches through the Steamworks partner
/// Web API, which is what turns an uploader into a release manager: you can see what
/// you shipped, and promote a build to a branch without opening a browser.
///
/// Response shapes for these endpoints are not documented publicly and have changed
/// shape between revisions, so parsing walks the JSON looking for the fields it needs
/// instead of binding to a fixed schema. A response Valve reshapes should degrade to
/// missing fields, never to an exception.
/// </summary>
public sealed class PartnerApiClient : IDisposable
{
    private const string BaseUrl = "https://partner.steam-api.com/";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PartnerApiClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress ??= new Uri(BaseUrl);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    /// <summary>Most recent builds first.</summary>
    public async Task<IReadOnlyList<SteamBuild>> GetAppBuildsAsync(
        string publisherKey, uint appId, int count = 20, CancellationToken cancellation = default)
    {
        var url = $"ISteamApps/GetAppBuilds/v1/?key={Uri.EscapeDataString(publisherKey)}" +
                  $"&appid={appId}&count={count}";

        using var document = await GetJsonAsync(url, cancellation).ConfigureAwait(false);

        var builds = new List<SteamBuild>();
        foreach (var element in FindObjectsWith(document.RootElement, "BuildID", "buildid"))
        {
            var buildId = ReadUInt(element, "BuildID", "buildid");
            if (buildId is null or 0) continue;

            builds.Add(new SteamBuild(
                buildId.Value,
                ReadString(element, "Description", "description") ?? string.Empty,
                ReadUnixTime(element, "CreationTime", "creationtime", "TimeUpdated", "timeupdated"),
                ReadString(element, "AccountID", "accountid", "account"),
                ReadLiveBranches(element)));
        }

        return builds
            .GroupBy(b => b.BuildId).Select(g => g.First())   // guard against nested duplicates
            .OrderByDescending(b => b.BuildId)
            .ToList();
    }

    public async Task<IReadOnlyList<SteamBranch>> GetAppBetasAsync(
        string publisherKey, uint appId, CancellationToken cancellation = default)
    {
        var url = $"ISteamApps/GetAppBetas/v1/?key={Uri.EscapeDataString(publisherKey)}&appid={appId}";

        using var document = await GetJsonAsync(url, cancellation).ConfigureAwait(false);

        var branches = new List<SteamBranch>();

        // Branches come back keyed by branch name, so the name lives on the property
        // rather than inside the object.
        foreach (var container in FindContainersOfNamedObjects(document.RootElement))
        {
            foreach (var property in container.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var buildId = ReadUInt(property.Value, "BuildID", "buildid");
                if (buildId is null) continue;

                branches.Add(new SteamBranch(
                    property.Name,
                    buildId.Value,
                    ReadString(property.Value, "Description", "description") ?? string.Empty,
                    ReadUnixTime(property.Value, "TimeUpdated", "timeupdated"),
                    ReadBool(property.Value, "PwdRequired", "pwdrequired")));
            }
        }

        return branches
            .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Promotes a build to a branch. <paramref name="branch"/> is <c>public</c> for the
    /// default branch, in which case Steam additionally requires the SteamID of the
    /// account authorising the change on a released app.
    /// </summary>
    public async Task SetAppBuildLiveAsync(
        string publisherKey, uint appId, uint buildId, string branch,
        string? description = null, ulong? steamId = null,
        CancellationToken cancellation = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["key"] = publisherKey,
            ["appid"] = appId.ToString(CultureInfo.InvariantCulture),
            ["buildid"] = buildId.ToString(CultureInfo.InvariantCulture),
            ["betakey"] = branch
        };

        if (!string.IsNullOrWhiteSpace(description)) fields["description"] = description;
        if (steamId is not null) fields["steamid"] = steamId.Value.ToString(CultureInfo.InvariantCulture);

        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http
            .PostAsync("ISteamApps/SetAppBuildLive/v2/", content, cancellation)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new PartnerApiException(Explain(response.StatusCode, body), response.StatusCode);

        // A 200 can still carry a failure in the body.
        if (body.Contains("\"result\"", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("Failure", StringComparison.OrdinalIgnoreCase))
            throw new PartnerApiException($"Steam rejected the change: {body}");
    }

    // ------------------------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellation)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new PartnerApiException(
                "Could not reach partner.steam-api.com. Check your network connection.", null, e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new PartnerApiException(Explain(response.StatusCode, body), response.StatusCode);

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new PartnerApiException("Steam returned a response that was not JSON.", null, e);
            }
        }
    }

    private static string Explain(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Forbidden =>
            "Steam rejected the publisher Web API key (403). Check that the key belongs to " +
            "this app's publisher group and has the right permissions.",
        HttpStatusCode.Unauthorized =>
            "The publisher Web API key is missing or invalid (401).",
        HttpStatusCode.TooManyRequests =>
            "Rate limited by Steam (429). Wait a minute and try again.",
        _ => $"Steam returned {(int)status} {status}. {Truncate(body, 300)}"
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    // ---- schema-tolerant readers ----

    private static IEnumerable<JsonElement> FindObjectsWith(JsonElement element, params string[] anyKey)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (anyKey.Any(k => element.TryGetProperty(k, out _)))
                yield return element;

            foreach (var property in element.EnumerateObject())
                foreach (var nested in FindObjectsWith(property.Value, anyKey))
                    yield return nested;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindObjectsWith(item, anyKey))
                    yield return nested;
        }
    }

    /// <summary>Objects whose properties are themselves objects carrying a BuildID.</summary>
    private static IEnumerable<JsonElement> FindContainersOfNamedObjects(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) yield break;

        var holdsNamedBuilds = element.EnumerateObject().Any(p =>
            p.Value.ValueKind == JsonValueKind.Object &&
            (p.Value.TryGetProperty("BuildID", out _) || p.Value.TryGetProperty("buildid", out _)));

        if (holdsNamedBuilds) yield return element;

        foreach (var property in element.EnumerateObject())
            foreach (var nested in FindContainersOfNamedObjects(property.Value))
                yield return nested;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null
            };
        }
        return null;
    }

    private static uint? ReadUInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
                return number;

            // Steam frequently returns numeric IDs as strings.
            if (value.ValueKind == JsonValueKind.String &&
                uint.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out var parsed))
                return parsed;
        }
        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
                JsonValueKind.String => value.GetString() is "1" or "true" or "True",
                _ => false
            };
        }
        return false;
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            long seconds;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out seconds))
                return FromUnix(seconds);

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out seconds))
                return FromUnix(seconds);
        }
        return null;
    }

    private static DateTimeOffset? FromUnix(long seconds) =>
        seconds <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static IReadOnlyList<string> ReadLiveBranches(JsonElement element)
    {
        var branches = new List<string>();

        foreach (var name in new[] { "BetaKeys", "betakeys", "Branches", "branches" })
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    branches.AddRange((value.GetString() ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case JsonValueKind.Array:
                    branches.AddRange(value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()!));
                    break;
                case JsonValueKind.Object:
                    branches.AddRange(value.EnumerateObject().Select(p => p.Name));
                    break;
            }
        }

        return branches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

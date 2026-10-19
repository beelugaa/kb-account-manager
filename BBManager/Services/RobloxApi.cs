using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBManager.Services;

public record RobloxUser(long Id, string Name, string DisplayName);

public static class RobloxApi
{
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,                  // une redirection ne doit jamais emmener le cookie ailleurs
        AutomaticDecompression = DecompressionMethods.All,
        CheckCertificateRevocationList = true,
    })
    { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2_000_000 };

    /// <summary>Toutes les requêtes (et donc tous les cookies) ne partent que vers du HTTPS *.roblox.com.</summary>
    private static void EnsureRobloxHost(string url)
    {
        var u = new Uri(url);
        if (u.Scheme != Uri.UriSchemeHttps || !(u.Host == "roblox.com" || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Requête refusée : destination non autorisée.");
    }

    /// <summary>Nettoie un cookie collé : retire "Cookie:", ".ROBLOSECURITY=", guillemets, espaces, point-virgule.</summary>
    public static string NormalizeCookie(string raw)
    {
        raw = raw.Trim().Trim('"');
        var m = Regex.Match(raw, @"\.ROBLOSECURITY\s*=\s*([^;\s]+)");
        if (m.Success) raw = m.Groups[1].Value;
        var idx = raw.IndexOf("_|WARNING", StringComparison.Ordinal);
        if (idx > 0) raw = raw[idx..];
        return raw.Trim().Trim(';', '"');
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? cookie = null, string? csrf = null, HttpContent? body = null)
    {
        EnsureRobloxHost(url);
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.TryAddWithoutValidation("User-Agent", UA);
        r.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        r.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        if (cookie != null) r.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");
        if (csrf != null) r.Headers.TryAddWithoutValidation("x-csrf-token", csrf);
        return r;
    }

    /// <summary>Retourne l'utilisateur du cookie, ou null si le cookie est invalide/expiré.</summary>
    public static async Task<RobloxUser?> GetAuthenticatedUserAsync(string cookie)
    {
        using var resp = await Http.SendAsync(Req(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie));
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new RobloxUser(root.GetProperty("id").GetInt64(), root.GetProperty("name").GetString() ?? "",
            root.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "");
    }

    public static async Task<long> GetRobuxAsync(string cookie)
    {
        try
        {
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, "https://economy.roblox.com/v1/user/currency", cookie));
            if (!resp.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("robux", out var r) ? r.GetInt64() : 0;
        }
        catch { return 0; }
    }

    public static async Task<string> GetAvatarUrlAsync(long userId)
    {
        try
        {
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, url));
            if (!resp.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            return data.GetArrayLength() > 0 ? data[0].GetProperty("imageUrl").GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public static async Task<string?> GetPlaceNameAsync(long placeId)
    {
        try
        {
            using var r1 = await Http.SendAsync(Req(HttpMethod.Get, $"https://apis.roblox.com/universes/v1/places/{placeId}/universe"));
            if (!r1.IsSuccessStatusCode) return null;
            using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
            if (!d1.RootElement.TryGetProperty("universeId", out var u) || u.ValueKind != JsonValueKind.Number) return null;

            using var r2 = await Http.SendAsync(Req(HttpMethod.Get, $"https://games.roblox.com/v1/games?universeIds={u.GetInt64()}"));
            if (!r2.IsSuccessStatusCode) return null;
            using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
            var arr = d2.RootElement.GetProperty("data");
            return arr.GetArrayLength() > 0 ? arr[0].GetProperty("name").GetString() : null;
        }
        catch { return null; }
    }

    public static async Task<long?> GetUserIdByNameAsync(string username)
    {
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { usernames = new[] { username }, excludeBannedUsers = false }),
                Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(Req(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", null, null, body));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("data");
            return arr.GetArrayLength() > 0 ? arr[0].GetProperty("id").GetInt64() : null;
        }
        catch { return null; }
    }

    private static StringContent EmptyJson() => new("", Encoding.UTF8, "application/json");

    private static HttpRequestMessage TicketReq(string cookie, string? csrf)
    {
        var r = Req(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie, csrf, EmptyJson());
        r.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
        return r;
    }

    /// <summary>
    /// Jeton CSRF : on interroge l'endpoint de ticket sans jeton ; Roblox répond 403 en fournissant le jeton
    /// (aucun ticket n'est émis, et aucune action sur le compte n'est effectuée).
    /// </summary>
    private static async Task<string> GetCsrfAsync(string cookie)
    {
        using var resp = await Http.SendAsync(TicketReq(cookie, null));
        if (resp.Headers.TryGetValues("x-csrf-token", out var v)) return v.First();
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Cookie expiré ou invalide.");
        throw new InvalidOperationException($"Impossible d'obtenir le jeton CSRF (HTTP {(int)resp.StatusCode}).");
    }

    /// <summary>Ticket d'authentification à usage unique pour lancer le client avec ce compte.</summary>
    public static async Task<string> GetAuthTicketAsync(string cookie)
    {
        var csrf = await GetCsrfAsync(cookie);
        using var resp = await Http.SendAsync(TicketReq(cookie, csrf));
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Cookie expiré ou invalide.");
        if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var t)) return t.First();
        throw new InvalidOperationException($"Ticket refusé par Roblox (HTTP {(int)resp.StatusCode}).");
    }

    /// <summary>Résout un lien de partage de serveur privé (roblox.com/share?code=...&amp;type=Server).</summary>
    public static async Task<(long placeId, string linkCode)?> ResolveServerShareLinkAsync(string cookie, string code)
    {
        try
        {
            var csrf = await GetCsrfAsync(cookie);
            var body = new StringContent(JsonSerializer.Serialize(new { linkId = code, linkType = "Server" }), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(Req(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie, csrf, body));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var d = doc.RootElement.GetProperty("privateServerInviteData");
            return (d.GetProperty("placeId").GetInt64(), d.GetProperty("linkCode").GetString() ?? "");
        }
        catch { return null; }
    }
}

using System.Text;
using System.Text.Json;

namespace Lustral.UnityLibraries;

/// <summary>A Unity release and the modules offered for its macOS editor, whose packages contain the player files.</summary>
internal sealed record UnityRelease(UnityVersion Version, string Revision, IReadOnlyDictionary<string, UnityModule> Modules);

/// <param name="Md5">The installer's MD5 hash, as lowercase hexadecimal, if Unity provides it.</param>
internal sealed record UnityModule(string Id, Uri Url, string Type, long Size, string? Md5);

/// <summary>
/// Unity's public release API (<c>services.api.unity.com/unity/editor/release/v1/releases</c>), as used by Unity Hub.
/// Extended LTS releases are also requested, because games are built with them. The modules are those of the macOS (x64)
/// editor, whose installers are plain <c>.pkg</c> archives (see <see cref="MacPackage"/>), whereas the Windows installers
/// would have to be executed.
/// </summary>
internal static class UnityReleases
{
    private const string Endpoint = "https://services.api.unity.com/unity/editor/release/v1/releases";
    private const int PageSize = 25; // The API's maximum.

    public static async Task<IReadOnlyList<UnityRelease>> ListAsync(HttpClient http, CancellationToken cancellation)
    {
        var releases = new List<UnityRelease>();
        for (var offset = 0; ; offset += PageSize)
        {
            var url = $"{Endpoint}?limit={PageSize}&offset={offset}&platform=MAC_OS&architecture=X86_64&entitlements=XLTS";
            using var document = JsonDocument.Parse(await GetWithRetriesAsync(http, url, cancellation));
            var results = document.RootElement.GetProperty("results");
            foreach (var release in results.EnumerateArray())
            {
                if (Parse(release) is { } parsed)
                    releases.Add(parsed);
            }
            if (results.GetArrayLength() == 0 || offset + PageSize >= document.RootElement.GetProperty("total").GetInt32())
                break;
        }
        // The API lists a version once for each stream that contains it; only one entry is kept.
        return releases.GroupBy(r => r.Version).Select(g => g.First()).OrderByDescending(r => r.Version).ToList();
    }

    internal static UnityRelease? Parse(JsonElement release)
    {
        if (!UnityVersion.TryParse(release.GetProperty("version").GetString(), out var version))
            return null;
        var modules = new Dictionary<string, UnityModule>(StringComparer.Ordinal);
        foreach (var download in release.GetProperty("downloads").EnumerateArray())
        {
            if (download.GetProperty("platform").GetString() != "MAC_OS" || download.GetProperty("architecture").GetString() != "X86_64")
                continue;
            foreach (var module in download.GetProperty("modules").EnumerateArray())
            {
                var id = module.GetProperty("id").GetString();
                if (id is null || !Uri.TryCreate(module.GetProperty("url").GetString(), UriKind.Absolute, out var url))
                    continue;
                var size = module.TryGetProperty("downloadSize", out var downloadSize) ? downloadSize.GetProperty("value").GetInt64() : 0;
                var integrity = module.TryGetProperty("integrity", out var value) ? value.GetString() : null;
                modules[id] = new UnityModule(id, url, module.GetProperty("type").GetString() ?? "", size, Md5(integrity));
            }
        }
        return new UnityRelease(version, release.GetProperty("shortRevision").GetString() ?? "", modules);
    }

    /// <summary>
    /// Unity's <c>integrity</c> value is <c>md5-</c> followed by base64, which encodes either the hexadecimal digest as
    /// text (as the API currently returns) or the 16 raw bytes.
    /// </summary>
    internal static string? Md5(string? integrity)
    {
        if (integrity is null || !integrity.StartsWith("md5-", StringComparison.Ordinal))
            return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(integrity[4..]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.Length == 16)
            return Convert.ToHexStringLower(bytes);
        var text = Encoding.ASCII.GetString(bytes);
        return text.Length == 32 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
    }

    private static async Task<string> GetWithRetriesAsync(HttpClient http, string url, CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await http.GetStringAsync(url, cancellation);
            }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellation);
            }
        }
    }
}

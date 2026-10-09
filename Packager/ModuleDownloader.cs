using System.Security.Cryptography;

namespace Lustral.UnityLibraries;

/// <summary>Downloads a Unity module's installer from Unity's servers and verifies it against the MD5 hash provided by Unity's API.</summary>
internal static class ModuleDownloader
{
    public static async Task<string> DownloadAsync(HttpClient http, UnityModule module, string directory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(module.Url.AbsolutePath));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Console.WriteLine($"  downloading {module.Url} ({module.Size / (1 << 20)} MB)");
                using var response = await http.GetAsync(module.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var file = File.Create(path))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                    {
                        md5.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    }
                }
                var actual = Convert.ToHexStringLower(md5.GetHashAndReset());
                if (module.Md5 is { } expected && actual != expected)
                    throw new InvalidDataException($"MD5 mismatch for {Path.GetFileName(path)}: {actual}, expected {expected}");
                return path;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException && attempt < 3)
            {
                Console.WriteLine($"  download failed ({e.Message}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), cancellation);
            }
        }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;

namespace Lustral.UnityLibraries;

/// <summary>
/// Gets a Unity module's files the way Unity Hub does: downloads Unity's installer from Unity's servers, checks it
/// against the MD5 Unity's API gives, and runs it silently into a folder of its own. Windows module installers are
/// NSIS built for installers over 2 GB, which 7-Zip and other extractors can't open, so running them (on Windows, as
/// the GitHub runner does) is the dependable way to their files.
/// </summary>
internal static class ModuleInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    /// <returns>The folder the module installed into: the files are at the paths they'd have in Unity's editor folder.</returns>
    public static async Task<string> InstallAsync(HttpClient http, UnityModule module, string workDirectory, CancellationToken cancellation)
    {
        if (module.Type != "EXE")
            throw new NotSupportedException($"{module.Id} comes as {module.Type}; only Unity's Windows installers (EXE) are supported so far");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{module.Id}'s installer runs only on Windows (or pass --from-editor to read an installed editor)");

        Directory.CreateDirectory(workDirectory);
        var installer = Path.Combine(workDirectory, Path.GetFileName(module.Url.AbsolutePath));
        await DownloadAsync(http, module, installer, cancellation);

        // NSIS takes /D last and unquoted, and the folder must not exist yet with something else in it.
        var target = Path.Combine(workDirectory, module.Id);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Console.WriteLine($"  installing {module.Id} into {target}");
        var start = new ProcessStartInfo(installer, $"/S /D={target}") { UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {installer}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{module.Id}'s installer didn't finish in {InstallTimeout.TotalMinutes} minutes");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{module.Id}'s installer exited with code {process.ExitCode}");
        File.Delete(installer); // runners have little disk to spare
        return target;
    }

    private static async Task DownloadAsync(HttpClient http, UnityModule module, string path, CancellationToken cancellation)
    {
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
                    throw new InvalidDataException($"{Path.GetFileName(path)}'s MD5 is {actual}; Unity says {expected}");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException && attempt < 3)
            {
                Console.WriteLine($"  download failed ({e.Message}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), cancellation);
            }
        }
    }
}

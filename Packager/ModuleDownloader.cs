using System.Security.Cryptography;

namespace Lustral.UnityLibraries;

/// <summary>
/// Downloads a Unity module's installer from Unity's servers, extracting the wanted files while it downloads, and verifies
/// it against the MD5 hash provided by Unity's API. The installer itself is not stored, so that the editor's package,
/// several gigabytes, needs no disk space.
/// </summary>
internal static class ModuleDownloader
{
    /// <param name="wanted">Selects files by their path in the package (see <see cref="MacPackage.Extract(Stream, string, Func{string, bool})"/>).</param>
    /// <returns>The number of files written to <paramref name="destination"/>.</returns>
    public static async Task<int> ExtractAsync(HttpClient http, UnityModule module, string destination, Func<string, bool> wanted, CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                Console.WriteLine($"  downloading {module.Url} ({module.Size / (1 << 20)} MB)");
                using var response = await http.GetAsync(module.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                var hashed = new HashingStream(source, md5);
                var written = MacPackage.Extract(hashed, destination, wanted);
                hashed.CopyTo(Stream.Null); // The rest of the package, for its hash.
                var actual = Convert.ToHexStringLower(md5.GetHashAndReset());
                if (module.Md5 is { } expected && actual != expected)
                    throw new InvalidDataException($"MD5 mismatch for {Path.GetFileName(module.Url.AbsolutePath)}: {actual}, expected {expected}");
                return written;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException && attempt < 3)
            {
                Console.WriteLine($"  download failed ({e.Message}); retrying");
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), cancellation);
            }
        }
    }

    /// <summary>A read-only stream that adds what is read from another stream to a hash.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

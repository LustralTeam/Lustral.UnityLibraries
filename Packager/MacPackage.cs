using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Lustral.UnityLibraries;

/// <summary>
/// Reads Unity's macOS module installers (<c>.pkg</c>), the source also used by MelonLoader.UnityDependencies, without
/// requiring 7-Zip or macOS. A <c>.pkg</c> file is a xar archive whose <c>Payload</c> is a gzip-compressed cpio archive
/// ("odc" format) containing the module's files, at their paths within the editor's
/// <c>PlaybackEngines/&lt;platform&gt;Support</c> folder. The package is streamed once, and only the requested files are
/// written.
/// </summary>
internal static class MacPackage
{
    /// <param name="wanted">Selects files by their path in the payload (<c>Variations/.../UnityEngine.dll</c>, with forward slashes).</param>
    /// <returns>The number of files written.</returns>
    public static int Extract(string packagePath, string destination, Func<string, bool> wanted)
    {
        using var file = File.OpenRead(packagePath);
        var payloads = Payloads(file);
        if (payloads.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(packagePath)} has no Payload and is not a module installer package");
        var written = 0;
        foreach (var (offset, length) in payloads)
        {
            file.Position = offset;
            using var gzip = new GZipStream(new WindowStream(file, length), CompressionMode.Decompress, leaveOpen: true);
            written += ExtractCpio(gzip, destination, wanted);
        }
        return written;
    }

    /// <summary>Returns the location of each <c>Payload</c> in the xar archive: its absolute offset and length.</summary>
    internal static List<(long Offset, long Length)> Payloads(Stream xar)
    {
        Span<byte> header = stackalloc byte[28];
        xar.ReadExactly(header);
        if (!header[..4].SequenceEqual("xar!"u8))
            throw new InvalidDataException("Not a xar archive (a macOS .pkg)");
        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        var tocCompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        xar.Position = headerSize;
        XDocument toc;
        using (var zlib = new ZLibStream(new WindowStream(xar, tocCompressed), CompressionMode.Decompress, leaveOpen: true))
            toc = XDocument.Load(zlib);
        var heap = headerSize + tocCompressed;
        return toc.Descendants("file")
            .Where(f => (string?)f.Element("name") == "Payload" && f.Element("data") is not null)
            .Select(f =>
            {
                var data = f.Element("data")!;
                var encoding = (string?)data.Element("encoding")?.Attribute("style");
                if (encoding is not (null or "application/octet-stream"))
                    throw new InvalidDataException($"Payload is stored as {encoding}; only uncompressed xar entries are supported");
                return (heap + (long)data.Element("offset")!, (long)data.Element("length")!);
            })
            .ToList();
    }

    /// <summary>Old portable cpio format ("odc", <c>070707</c>): a 76-character octal header, followed by the name and the data.</summary>
    internal static int ExtractCpio(Stream cpio, string destination, Func<string, bool> wanted)
    {
        var root = Path.GetFullPath(destination);
        var header = new byte[76];
        var written = 0;
        while (true)
        {
            cpio.ReadExactly(header);
            if (!header.AsSpan(0, 6).SequenceEqual("070707"u8))
                throw new InvalidDataException($"Unexpected cpio header \"{Encoding.ASCII.GetString(header, 0, 6)}\": only the odc format is supported");
            var mode = Octal(header, 18, 6);
            var nameSize = (int)Octal(header, 59, 6);
            var fileSize = Octal(header, 65, 11);
            var nameBytes = new byte[nameSize];
            cpio.ReadExactly(nameBytes);
            var name = Encoding.UTF8.GetString(nameBytes, 0, Math.Max(0, nameSize - 1));
            if (name == "TRAILER!!!")
                return written;

            var path = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name.TrimStart('/');
            var isFile = (mode & 0xF000) == 0x8000;
            if (isFile && path.Length > 0 && wanted(path))
            {
                var target = Path.GetFullPath(Path.Combine(root, path));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException($"cpio entry {name} resolves outside the folder");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                Copy(cpio, output, fileSize);
                written++;
            }
            else
                Copy(cpio, Stream.Null, fileSize);
        }
    }

    private static long Octal(byte[] buffer, int offset, int length)
    {
        long value = 0;
        for (var i = offset; i < offset + length; i++)
        {
            var digit = buffer[i] - '0';
            if (digit is < 0 or > 7)
                throw new InvalidDataException("Malformed cpio header");
            value = value * 8 + digit;
        }
        return value;
    }

    private static void Copy(Stream source, Stream destination, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new EndOfStreamException("The cpio archive ends within a file");
            destination.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>A read-only view of a range of another stream, starting at its current position.</summary>
    private sealed class WindowStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
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

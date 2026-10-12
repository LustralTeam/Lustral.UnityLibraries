using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Lustral.UnityLibraries;

/// <summary>
/// Reads Unity's macOS installers (<c>.pkg</c>), the source also used by MelonLoader.UnityDependencies, without requiring
/// 7-Zip or macOS. A <c>.pkg</c> file is a xar archive whose <c>Payload</c> is a gzip-compressed cpio archive ("odc"
/// format) containing the files, at their paths within the installation folder: a module's within the editor's
/// <c>PlaybackEngines/&lt;platform&gt;Support</c> folder, the editor's within its own. The package is read once from start
/// to end, so it can be read while it downloads, and only the requested files are written.
/// </summary>
internal static class MacPackage
{
    /// <param name="wanted">Selects files by their path in the payload (<c>Variations/.../UnityEngine.dll</c>, with forward slashes).</param>
    /// <returns>The number of files written.</returns>
    public static int Extract(string packagePath, string destination, Func<string, bool> wanted)
    {
        using var file = File.OpenRead(packagePath);
        return Extract(file, destination, wanted);
    }

    /// <summary>
    /// Extracts from a package that is read sequentially, such as a download: the header and the table of contents, then
    /// the heap through the last <c>Payload</c>. The rest of the stream is not read.
    /// </summary>
    public static int Extract(Stream package, string destination, Func<string, bool> wanted)
    {
        var (heap, payloads) = TableOfContents(package);
        if (payloads.Count == 0)
            throw new InvalidDataException("The package has no Payload and is not an installer package");
        var position = heap;
        var written = 0;
        foreach (var (offset, length) in payloads.OrderBy(p => p.Offset))
        {
            if (offset < position)
                throw new InvalidDataException("The package's payloads overlap");
            Skip(package, offset - position);
            var window = new WindowStream(package, length);
            using (var gzip = new GZipStream(window, CompressionMode.Decompress, leaveOpen: true))
                written += ExtractCpio(gzip, destination, wanted);
            window.Drain(); // The cpio archive ends before its padding and the gzip trailer.
            position = offset + length;
        }
        return written;
    }

    /// <summary>
    /// Reads the xar header and table of contents, leaving the stream at the start of the heap. Returns the heap's offset in
    /// the archive and each <c>Payload</c>'s offset in the archive and length.
    /// </summary>
    internal static (long Heap, List<(long Offset, long Length)> Payloads) TableOfContents(Stream xar)
    {
        Span<byte> header = stackalloc byte[28];
        xar.ReadExactly(header);
        if (!header[..4].SequenceEqual("xar!"u8))
            throw new InvalidDataException("Not a xar archive (a macOS .pkg)");
        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        var tocCompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        if (headerSize < header.Length)
            throw new InvalidDataException("Malformed xar header");
        Skip(xar, headerSize - header.Length);
        XDocument toc;
        var tocWindow = new WindowStream(xar, tocCompressed);
        using (var zlib = new ZLibStream(tocWindow, CompressionMode.Decompress, leaveOpen: true))
            toc = XDocument.Load(zlib);
        tocWindow.Drain(); // The XML ends before the zlib checksum.
        var heap = headerSize + tocCompressed;
        return (heap, toc.Descendants("file")
            .Where(f => (string?)f.Element("name") == "Payload" && f.Element("data") is not null)
            .Select(f =>
            {
                var data = f.Element("data")!;
                var encoding = (string?)data.Element("encoding")?.Attribute("style");
                if (encoding is not (null or "application/octet-stream"))
                    throw new InvalidDataException($"Payload is stored as {encoding}; only uncompressed xar entries are supported");
                return (heap + (long)data.Element("offset")!, (long)data.Element("length")!);
            })
            .ToList());
    }

    private static void Skip(Stream stream, long count) => Copy(stream, Stream.Null, count);

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

        /// <summary>Reads the rest of the range, so that the inner stream is positioned after it.</summary>
        public void Drain() => Copy(this, Stream.Null, _remaining);

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

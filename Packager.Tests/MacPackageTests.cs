using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lustral.UnityLibraries.Tests;

public sealed class MacPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unitylibraries-tests", Guid.NewGuid().ToString("N"));

    public MacPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A package laid out as Unity's: a xar archive with an uncompressed TargetSupport.pkg.tmp/Payload containing a
    // gzip-compressed odc cpio archive.
    private string Package(params (string Name, string? Content)[] entries)
    {
        var path = Path.Combine(_root, "module.pkg");
        File.WriteAllBytes(path, PackageBytes("TargetSupport.pkg.tmp", 0, entries));
        return path;
    }

    // With bomSize, a Bom file of that size precedes the payload in the heap, as in the editor's package.
    private static byte[] PackageBytes(string component, int bomSize, (string Name, string? Content)[] entries)
    {
        var cpio = new MemoryStream();
        foreach (var (name, content) in entries)
            Entry(cpio, name, content is null ? 0x41ED : 0x81A4, content is null ? [] : Encoding.UTF8.GetBytes(content));
        Entry(cpio, "TRAILER!!!", 0, []);
        var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(cpio.ToArray());

        var bom = bomSize == 0 ? "" : $"""
            <file id="4"><data><length>{bomSize}</length><encoding style="application/octet-stream"/><offset>20</offset><size>{bomSize}</size></data><name>Bom</name><type>file</type></file>
            """;
        var toc = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?><xar><toc><file id="1"><name>Resources</name><type>directory</type></file>
            <file id="2"><name>{component}</name><type>directory</type>{bom}<file id="3"><data><length>{payload.Length}</length>
            <encoding style="application/octet-stream"/><offset>{20 + bomSize}</offset><size>{payload.Length}</size></data><name>Payload</name><type>file</type></file></file></toc></xar>
            """);
        var compressedToc = new MemoryStream();
        using (var zlib = new ZLibStream(compressedToc, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(toc);

        var header = new byte[28];
        "xar!"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)compressedToc.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), (ulong)toc.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 1);
        var file = new MemoryStream();
        file.Write(header);
        file.Write(compressedToc.ToArray());
        file.Write(new byte[20]); // The heap starts with the TOC checksum.
        file.Write(new byte[bomSize]);
        file.Write(payload.ToArray());
        return file.ToArray();
    }

    private static void Entry(Stream cpio, string name, int mode, byte[] data)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        cpio.Write(Encoding.ASCII.GetBytes(Odc(mode, nameBytes.Length, data.Length)));
        cpio.Write(nameBytes);
        cpio.Write(data);
    }

    private static string Odc(int mode, int nameSize, int fileSize) =>
        "070707" + "000000" + "000000" + Convert.ToString(mode, 8).PadLeft(6, '0') + "000000" + "000000" + "000001" + "000000" +
        "00000000000" + Convert.ToString(nameSize, 8).PadLeft(6, '0') + Convert.ToString(fileSize, 8).PadLeft(11, '0');

    [Fact]
    public void Writes_the_wanted_files_of_the_payload_at_their_paths()
    {
        var package = Package(
            ("./Variations", null),
            ("./Variations/win64_player_nondevelopment_mono/Data/Managed/UnityEngine.CoreModule.dll", "core"),
            ("./Variations/win64_player_nondevelopment_mono/Data/Managed/UnityEngine.CoreModule.pdb", "symbols"),
            ("./Variations/win64_player_development_mono/Data/Managed/UnityEngine.CoreModule.dll", "development"),
            ("./WinPlayerBuildProgram.Data.dll", "tool"));

        var written = MacPackage.Extract(package, Path.Combine(_root, "out"), path => path.StartsWith("Variations/win64_player_nondevelopment_mono/", StringComparison.Ordinal));

        Assert.Equal(2, written);
        var managed = Path.Combine(_root, "out", "Variations", "win64_player_nondevelopment_mono", "Data", "Managed");
        Assert.Equal("core", File.ReadAllText(Path.Combine(managed, "UnityEngine.CoreModule.dll")));
        Assert.Equal("symbols", File.ReadAllText(Path.Combine(managed, "UnityEngine.CoreModule.pdb")));
        Assert.False(Directory.Exists(Path.Combine(_root, "out", "Variations", "win64_player_development_mono")));
    }

    [Fact]
    public void Reads_a_package_from_start_to_end_as_it_downloads()
    {
        var package = PackageBytes("Unity.pkg.tmp", 1000,
        [
            ("./Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32/mscorlib.dll", "corlib"),
            ("./Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32/Facades/netstandard.dll", "facade"),
            ("./Unity.app/Contents/MonoBleedingEdge/lib/mono/unityjit-win32/mscorlib.dll", "jit"),
        ]);
        using var download = new SequentialStream(package);

        var written = MacPackage.Extract(download, Path.Combine(_root, "out"),
            path => path.StartsWith("Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32/", StringComparison.Ordinal));

        Assert.Equal(2, written);
        var profile = Path.Combine(_root, "out", "Unity.app", "Contents", "MonoBleedingEdge", "lib", "mono", "unityaot-win32");
        Assert.Equal("corlib", File.ReadAllText(Path.Combine(profile, "mscorlib.dll")));
        Assert.Equal("facade", File.ReadAllText(Path.Combine(profile, "Facades", "netstandard.dll")));
        Assert.Equal(package.Length, download.Consumed); // The payload ends the package, and its window is read through.
    }

    [Fact]
    public void Paths_leaving_the_folder_are_refused()
    {
        var package = Package(("./../escape.dll", "x"));

        Assert.Throws<InvalidDataException>(() => MacPackage.Extract(package, Path.Combine(_root, "out"), _ => true));
    }

    [Theory]
    [InlineData("Variations/win64_player_nondevelopment_mono/Data/Managed/UnityEngine.dll", "Variations/win64_player_nondevelopment_mono")]
    [InlineData("Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32/Facades/netstandard.dll", "Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32")]
    [InlineData("Unity.app/Contents/MonoBleedingEdge/lib/mono", null)]
    [InlineData("Unity.app/Contents/Managed/UnityEngine.dll", null)]
    public void Describes_a_packages_layout_by_its_variations_and_class_library_profiles(string path, string? folder) =>
        Assert.Equal(folder, PackageBuilder.LayoutFolder(path));

    [Fact]
    public void Other_files_are_refused()
    {
        var path = Path.Combine(_root, "not.pkg");
        File.WriteAllBytes(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => MacPackage.Extract(path, Path.Combine(_root, "out"), _ => true));
    }

    // A stream that can only be read forward, like a download.
    private sealed class SequentialStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public long Consumed => _inner.Position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

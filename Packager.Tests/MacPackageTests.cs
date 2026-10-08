using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lustral.UnityLibraries.Tests;

public sealed class MacPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unitylibraries-tests", Guid.NewGuid().ToString("N"));

    public MacPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A package laid out like Unity's: xar, TargetSupport.pkg.tmp/Payload stored as is, a gzip of an odc cpio archive.
    private string Package(params (string Name, string? Content)[] entries)
    {
        var cpio = new MemoryStream();
        foreach (var (name, content) in entries)
            Entry(cpio, name, content is null ? 0x41ED : 0x81A4, content is null ? [] : Encoding.UTF8.GetBytes(content));
        Entry(cpio, "TRAILER!!!", 0, []);
        var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(cpio.ToArray());

        var toc = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?><xar><toc><file id="1"><name>Resources</name><type>directory</type></file>
            <file id="2"><name>TargetSupport.pkg.tmp</name><type>directory</type><file id="3"><data><length>{payload.Length}</length>
            <encoding style="application/octet-stream"/><offset>20</offset><size>{payload.Length}</size></data><name>Payload</name><type>file</type></file></file></toc></xar>
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
        var path = Path.Combine(_root, "module.pkg");
        using var file = File.Create(path);
        file.Write(header);
        file.Write(compressedToc.ToArray());
        file.Write(new byte[20]); // the heap starts with the TOC's checksum
        file.Write(payload.ToArray());
        return path;
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
    public void Paths_leaving_the_folder_are_refused()
    {
        var package = Package(("./../escape.dll", "x"));

        Assert.Throws<InvalidDataException>(() => MacPackage.Extract(package, Path.Combine(_root, "out"), _ => true));
    }

    [Fact]
    public void Other_files_are_refused()
    {
        var path = Path.Combine(_root, "not.pkg");
        File.WriteAllBytes(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => MacPackage.Extract(path, Path.Combine(_root, "out"), _ => true));
    }
}

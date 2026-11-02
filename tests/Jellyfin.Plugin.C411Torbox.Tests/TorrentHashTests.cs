using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class TorrentHashTests
{
    [Fact]
    public void FromTorrentFile_HashesInfoDictionary()
    {
        var (torrent, info) = Bencode.MakeTorrent("Film.2024.1080p.mkv");
        var expected = Convert.ToHexStringLower(SHA1.HashData(info));
        Assert.Equal(expected, TorrentHash.FromTorrentFile(torrent));
    }

    [Fact]
    public void FromTorrentFile_HandlesNestedListsAndDicts()
    {
        var info = "d5:filesld6:lengthi5e4:pathl1:aeed6:lengthi7e4:pathl1:b1:ceee4:name3:dir12:piece lengthi16384e6:pieces20:bbbbbbbbbbbbbbbbbbbbe";
        var torrent = Encoding.ASCII.GetBytes($"d13:announce-listll3:udpee4:info{info}8:url-listl0:ee");
        var expected = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(info)));
        Assert.Equal(expected, TorrentHash.FromTorrentFile(torrent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>login</html>")]
    [InlineData("d4:infod4:name")]
    [InlineData("d8:announce3:abce")]
    public void FromTorrentFile_ReturnsNullOnInvalidData(string data)
        => Assert.Null(TorrentHash.FromTorrentFile(Encoding.ASCII.GetBytes(data)));

    [Fact]
    public void FromMagnet_ReadsHexAndBase32()
    {
        const string hex = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";
        Assert.Equal(hex, TorrentHash.FromMagnet($"magnet:?xt=urn:btih:{hex.ToUpperInvariant()}&dn=x"));

        // Même hash encodé en base32.
        Assert.Equal(hex, TorrentHash.FromMagnet("magnet:?xt=urn:btih:YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK&dn=x"));
        Assert.Null(TorrentHash.FromMagnet("magnet:?dn=sans-hash"));
        Assert.Null(TorrentHash.FromMagnet(null));
    }

    [Theory]
    [InlineData(" C12FE1C06BBA254A9DC9F519B335AA7C1367A88A ", "c12fe1c06bba254a9dc9f519b335aa7c1367a88a")]
    [InlineData("pas-un-hash", null)]
    [InlineData("", null)]
    public void Normalize(string? input, string? expected) => Assert.Equal(expected, TorrentHash.Normalize(input));
}

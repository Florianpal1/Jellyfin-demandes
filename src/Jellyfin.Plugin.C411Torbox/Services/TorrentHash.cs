using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Calcul / extraction de l'infohash BitTorrent v1 (40 caractères hexadécimaux, minuscules).</summary>
public static partial class TorrentHash
{
    /// <summary>Calcule l'infohash d'un fichier .torrent (SHA-1 du dictionnaire « info » bencodé).</summary>
    /// <returns>L'infohash, ou null si le fichier est invalide.</returns>
    public static string? FromTorrentFile(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length == 0 || data[0] != (byte)'d')
            {
                return null;
            }

            int pos = 1;
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                var key = ReadString(data, ref pos);
                int valueStart = pos;
                SkipValue(data, ref pos);
                if (key.SequenceEqual("info"u8))
                {
                    return Convert.ToHexStringLower(SHA1.HashData(data[valueStart..pos]));
                }
            }

            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Extrait l'infohash d'un lien magnet (hex ou base32).</summary>
    public static string? FromMagnet(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet))
        {
            return null;
        }

        var match = BtihRegex().Match(magnet);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value;
        if (value.Length == 40)
        {
            return value.ToLowerInvariant();
        }

        var bytes = Base32Decode(value);
        return bytes is { Length: 20 } ? Convert.ToHexStringLower(bytes) : null;
    }

    /// <summary>Normalise un infohash fourni par un tiers ; null si invalide.</summary>
    public static string? Normalize(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var h = hash.Trim().ToLowerInvariant();
        return HexHashRegex().IsMatch(h) ? h : null;
    }

    private static ReadOnlySpan<byte> ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        int colon = data[pos..].IndexOf((byte)':');
        if (colon <= 0)
        {
            throw new FormatException("Chaîne bencode invalide.");
        }

        int length = int.Parse(Encoding.ASCII.GetString(data.Slice(pos, colon)), System.Globalization.CultureInfo.InvariantCulture);
        int start = pos + colon + 1;
        if (length < 0 || start + length > data.Length)
        {
            throw new FormatException("Longueur bencode invalide.");
        }

        pos = start + length;
        return data.Slice(start, length);
    }

    private static void SkipValue(ReadOnlySpan<byte> data, ref int pos)
    {
        byte c = data[pos];
        switch (c)
        {
            case (byte)'i':
                int end = data[pos..].IndexOf((byte)'e');
                if (end < 0)
                {
                    throw new FormatException("Entier bencode invalide.");
                }

                pos += end + 1;
                break;
            case (byte)'l':
                pos++;
                while (data[pos] != (byte)'e')
                {
                    SkipValue(data, ref pos);
                }

                pos++;
                break;
            case (byte)'d':
                pos++;
                while (data[pos] != (byte)'e')
                {
                    ReadString(data, ref pos);
                    SkipValue(data, ref pos);
                }

                pos++;
                break;
            case >= (byte)'0' and <= (byte)'9':
                ReadString(data, ref pos);
                break;
            default:
                throw new FormatException("Valeur bencode inconnue.");
        }
    }

    private static byte[]? Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new byte[input.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (char ch in input.ToUpperInvariant())
        {
            int val = Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (val < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return output;
    }

    [GeneratedRegex(@"urn:btih:([a-zA-Z0-9]{32,40})", RegexOptions.IgnoreCase)]
    private static partial Regex BtihRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex HexHashRegex();
}

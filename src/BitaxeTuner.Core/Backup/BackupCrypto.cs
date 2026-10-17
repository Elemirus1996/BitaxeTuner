using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Backup;

/// <summary>
/// 0.9.23: Sicherung vor dem Hochladen verschlüsseln (AES-256-GCM, Schlüssel per PBKDF2-SHA256 aus dem eigenen Passwort).
/// Aufbau: „BTENC1“, Salz (16), Runden (4), Nonce-Anfang (8), dann Blöcke à 1 MiB: Länge (4, oberstes Bit = letzter Block),
/// Geheimtext, Prüfwert (16). Nonce je Block = Anfang + Blocknummer; die Blocknummer und „letzter Block“ sind mit
/// abgesichert – Blöcke lassen sich weder vertauschen noch abschneiden.
/// </summary>
public static class BackupCrypto
{
    public const string Extension = ".enc";
    private static readonly byte[] Magic = "BTENC1"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;
    private const uint LastFlag = 0x8000_0000;
    public const int DefaultIterations = 600_000;

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Nonce(byte[] prefix, uint index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] Associated(uint index, bool last)
    {
        var ad = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(ad, index);
        ad[4] = last ? (byte)1 : (byte)0;
        return ad;
    }

    public static void Encrypt(Stream input, Stream output, string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(L.T("Für die Sicherung ins Netz fehlt das Verschlüsselungs-Passwort."));
        var salt = RandomNumberGenerator.GetBytes(16);
        var prefix = RandomNumberGenerator.GetBytes(8);
        using var aes = new AesGcm(DeriveKey(password, salt, iterations), 16);
        output.Write(Magic);
        output.Write(salt);
        Span<byte> num = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(num, iterations);
        output.Write(num);
        output.Write(prefix);

        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var currentLen = ReadFull(input, current);
        uint index = 0;
        while (true)
        {
            var nextLen = currentLen == ChunkSize ? ReadFull(input, next) : 0;
            var last = nextLen == 0;
            var cipher = new byte[currentLen];
            var tag = new byte[16];
            aes.Encrypt(Nonce(prefix, index), current.AsSpan(0, currentLen), cipher, tag, Associated(index, last));
            BinaryPrimitives.WriteUInt32BigEndian(num, (uint)currentLen | (last ? LastFlag : 0));
            output.Write(num);
            output.Write(cipher);
            output.Write(tag);
            if (last) return;
            (current, next) = (next, current);
            currentLen = nextLen;
            index++;
        }
    }

    /// <summary>Entschlüsseln; <see cref="InvalidDataException"/> bei falschem Passwort, beschädigter oder gekürzter Datei.</summary>
    public static void Decrypt(Stream input, Stream output, string password)
    {
        var header = new byte[Magic.Length + 16 + 4 + 8];
        if (ReadFull(input, header) != header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException(L.T("Keine verschlüsselte BitaxeTuner-Sicherung."));
        var salt = header[Magic.Length..(Magic.Length + 16)];
        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(Magic.Length + 16, 4));
        if (iterations is < 10_000 or > 10_000_000) throw new InvalidDataException(L.T("Keine verschlüsselte BitaxeTuner-Sicherung."));
        var prefix = header[(Magic.Length + 20)..];
        using var aes = new AesGcm(DeriveKey(password, salt, iterations), 16);
        var num = new byte[4];
        uint index = 0;
        while (true)
        {
            if (ReadFull(input, num) != 4) throw new InvalidDataException(L.T("Verschlüsselte Sicherung ist unvollständig."));
            var raw = BinaryPrimitives.ReadUInt32BigEndian(num);
            var last = (raw & LastFlag) != 0;
            var len = (int)(raw & ~LastFlag);
            if (len > ChunkSize) throw new InvalidDataException(L.T("Verschlüsselte Sicherung ist beschädigt."));
            var cipher = new byte[len];
            var tag = new byte[16];
            if (ReadFull(input, cipher) != len || ReadFull(input, tag) != 16) throw new InvalidDataException(L.T("Verschlüsselte Sicherung ist unvollständig."));
            var plain = new byte[len];
            try { aes.Decrypt(Nonce(prefix, index), cipher, tag, plain, Associated(index, last)); }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException(index == 0
                    ? L.T("Falsches Verschlüsselungs-Passwort oder beschädigte Sicherung.")
                    : L.T("Verschlüsselte Sicherung ist beschädigt."));
            }
            output.Write(plain);
            if (last) return;
            index++;
        }
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

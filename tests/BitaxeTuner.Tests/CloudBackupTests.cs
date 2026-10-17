using System.Net;
using System.Security.Cryptography;
using System.Text;
using BitaxeTuner.Core.Backup;
using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Profiles;
using BitaxeTuner.Core.Simulation;
using BitaxeTuner.Core.Transfer;

namespace BitaxeTuner.Tests;

/// <summary>0.9.23 Sicherung ins Netz: Verschlüsselung, S3-Signatur, WebDAV/S3 mit nachgebildetem Dienst, Ablauf im Hub.</summary>
public class CloudBackupTests
{
    private static byte[] Roundtrip(byte[] data, string password, string? decryptWith = null)
    {
        using var enc = new MemoryStream();
        BackupCrypto.Encrypt(new MemoryStream(data), enc, password, iterations: 10_000);
        using var dec = new MemoryStream();
        BackupCrypto.Decrypt(new MemoryStream(enc.ToArray()), dec, decryptWith ?? password);
        return dec.ToArray();
    }

    [Fact]
    public void Encryption_roundtrips_and_rejects_wrong_password_and_damage()
    {
        var data = RandomNumberGenerator.GetBytes(2_500_000);       // drei Blöcke
        Assert.Equal(data, Roundtrip(data, "richtig-langes-passwort"));
        Assert.Empty(Roundtrip([], "richtig-langes-passwort"));
        Assert.Throws<InvalidDataException>(() => Roundtrip(data, "richtig-langes-passwort", "falsch-langes-passwort"));

        using var enc = new MemoryStream();
        BackupCrypto.Encrypt(new MemoryStream(data), enc, "pw-1234567890", iterations: 10_000);
        var bytes = enc.ToArray();
        Assert.DoesNotContain(Encoding.ASCII.GetBytes("pw-1234567890"), new[] { bytes.Take(64).ToArray() });
        var flipped = (byte[])bytes.Clone();
        flipped[^100] ^= 1;
        Assert.Throws<InvalidDataException>(() => BackupCrypto.Decrypt(new MemoryStream(flipped), new MemoryStream(), "pw-1234567890"));
        // gekürzt: letzter Block fehlt
        Assert.Throws<InvalidDataException>(() => BackupCrypto.Decrypt(new MemoryStream(bytes[..(1024 * 1024 + 2000)]), new MemoryStream(), "pw-1234567890"));
        Assert.Throws<InvalidDataException>(() => BackupCrypto.Decrypt(new MemoryStream("PK\x03\x04 kein Archiv"u8.ToArray()), new MemoryStream(), "x"));
        Assert.Throws<InvalidOperationException>(() => BackupCrypto.Encrypt(new MemoryStream(data), new MemoryStream(), ""));
    }

    [Fact]
    public void S3_signature_matches_the_aws_example()
    {
        // AWS-Dokumentation „Signature Calculations for the Authorization Header“, Beispiel GET Object
        var headers = S3Signer.Sign("GET", "examplebucket.s3.amazonaws.com", "/test.txt", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Range"] = "bytes=0-9" }, S3Signer.EmptyHash,
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
                     "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            headers["Authorization"]);
        Assert.Equal("a=1&list-type=2&prefix=Bitaxe%2FTuner%2F", S3Signer.CanonicalQuery(new Dictionary<string, string> { ["prefix"] = "Bitaxe/Tuner/", ["list-type"] = "2", ["a"] = "1" }));
    }

    /// <summary>Kleiner WebDAV-Dienst im Speicher (MKCOL, PUT, MOVE, GET, DELETE, PROPFIND).</summary>
    private sealed class FakeDav : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public bool Folder;
        public string? Auth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Auth = req.Headers.Authorization?.ToString();
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            var name = path.Split('/').Last();
            switch (req.Method.Method)
            {
                case "MKCOL":
                    if (Folder) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                    Folder = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "PUT":
                    Files[name] = await req.Content!.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "MOVE":
                    var dest = Uri.UnescapeDataString(new Uri(req.Headers.GetValues("Destination").Single()).AbsolutePath).Split('/').Last();
                    Files[dest] = Files[name];
                    Files.Remove(name);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "GET":
                    return Files.TryGetValue(name, out var f) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f) } : new HttpResponseMessage(HttpStatusCode.NotFound);
                case "DELETE":
                    return new HttpResponseMessage(Files.Remove(name) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
                case "PROPFIND":
                    var hrefs = string.Concat(Files.Keys.Select(k => $"<d:response><d:href>/dav/BitaxeTuner/{Uri.EscapeDataString(k)}</d:href></d:response>"));
                    return new HttpResponseMessage((HttpStatusCode)207)
                    {
                        Content = new StringContent($"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/BitaxeTuner/</d:href></d:response>{hrefs}</d:multistatus>"""),
                    };
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    /// <summary>S3-Dienst im Speicher (Pfad-Adressierung): PUT, GET, DELETE, ListObjectsV2; prüft die SigV4-Kopfzeilen.</summary>
    private sealed class FakeS3 : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Objects = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=KEYID/", req.Headers.GetValues("Authorization").Single());
            Assert.Single(req.Headers.GetValues("x-amz-content-sha256"));
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);   // /bucket/key
            Assert.StartsWith("/bucket/", path);
            var key = path["/bucket/".Length..];
            if (req.Method == HttpMethod.Put)
            {
                var body = await req.Content!.ReadAsByteArrayAsync(ct);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), req.Headers.GetValues("x-amz-content-sha256").Single());
                Objects[key] = body;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Get && key.Length == 0)
            {
                var prefix = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["prefix"] ?? "";
                var keys = string.Concat(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => $"<Contents><Key>{k}</Key></Contents>"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"""<?xml version="1.0"?><ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated>{keys}</ListBucketResult>"""),
                };
            }
            if (req.Method == HttpMethod.Get)
                return Objects.TryGetValue(key, out var o) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(o) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>") };
            if (req.Method == HttpMethod.Delete)
            {
                Objects.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    private static async Task Roundtrip(IRemoteStore store, Func<IEnumerable<string>> remoteNames)
    {
        using var dir = new TempDir();
        var data = RandomNumberGenerator.GetBytes(300_000);
        File.WriteAllBytes(dir.File("a.zip"), data);
        var target = new EncryptedBackupTarget(store, "mein-sicherungs-passwort", dir.File("work"));
        var name = BackupNames.For(new DateTime(2026, 10, 1, 3, 0, 0));
        await target.UploadAsync(dir.File("a.zip"), name, default);
        Assert.Equal([name + ".enc"], remoteNames());
        Assert.Contains(name + ".enc", await target.ListAsync(default));
        await target.DownloadAsync(name + ".enc", dir.File("back.zip"), default);
        Assert.Equal(data, File.ReadAllBytes(dir.File("back.zip")));
        Assert.Empty(Directory.GetFiles(dir.File("work")));                          // keine Reste
        await target.DeleteAsync(name + ".enc", default);
        Assert.Empty(remoteNames());
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.DeleteAsync("fremde-datei.txt", default));
    }

    [Fact]
    public async Task WebDav_store_uploads_verifies_lists_and_deletes()
    {
        var dav = new FakeDav();
        using var store = new WebDavStore("https://cloud.example.org/dav/BitaxeTuner", "nutzer", "app-pass", dav);
        await Roundtrip(store, () => dav.Files.Keys.ToList());
        Assert.StartsWith("Basic ", dav.Auth);
        Assert.Throws<InvalidOperationException>(() => new WebDavStore("ftp://x", "", ""));
    }

    [Fact]
    public async Task S3_store_uploads_verifies_lists_and_deletes()
    {
        var s3 = new FakeS3();
        using var store = new S3Store("https://s3.example.org", "eu-central-003", "bucket", "BitaxeTuner", "KEYID", "geheim", s3);
        await Roundtrip(store, () => s3.Objects.Keys.Select(k => k["BitaxeTuner/".Length..]).ToList());
    }

    [Fact]
    public async Task Hub_backs_up_encrypted_into_the_network_and_keeps_only_the_newest()
    {
        using var dir = new TempDir();
        var dav = new FakeDav();
        var gamma = ProfileRegistry.LoadBuiltIn().First(p => p.Id == "bitaxe-gamma");
        var config = new AppConfig();
        config.Devices.Add(new DeviceConfig { Name = "Gamma", Host = "10.0.6.1" });
        using var hub = new MinerHub(config, new MinerHubOptions
        {
            DataDirectory = dir.Path,
            OnlineChecks = false,
            ClientFactory = h => new SimulatedMinerClient(gamma, 1, h),
            BackupHttpHandler = dav,
        });
        hub.Notify.TransportOverride = (_, _, _) => Task.CompletedTask;
        await hub.PollNowAsync();
        hub.Config.Backup.Cloud = new BackupCloudTarget { Enabled = true, Kind = "webdav", Url = "https://cloud.example.org/dav/BitaxeTuner/", User = "u" };
        hub.Config.Backup.Keep = 2;

        // ohne Verschlüsselungs-Passwort: Fehler beim Ziel, lokale Sicherung trotzdem da
        var failed = await hub.RunBackupAsync(new DateTime(2026, 10, 1, 3, 0, 0));
        Assert.False(failed.LastOk);
        Assert.Contains(failed.Targets, x => !x.Ok && x.Message.Contains("Verschlüsselungs-Passwort"));
        Assert.NotNull(failed.LastFile);

        hub.Secrets.Set(SecretStore.CloudSecret, "app-pass");
        hub.Secrets.Set(SecretStore.CloudEncryption, "mein-sicherungs-passwort");
        for (var day = 1; day <= 3; day++)
            Assert.True((await hub.RunBackupAsync(new DateTime(2026, 10, 1 + day, 3, 0, 0))).LastOk);
        Assert.Equal(2, dav.Files.Count);
        Assert.All(dav.Files.Keys, k => Assert.EndsWith(".zip.enc", k));
        Assert.Contains(BackupNames.For(new DateTime(2026, 10, 4, 3, 0, 0)) + ".enc", dav.Files.Keys);

        // holen, entschlüsseln, als echtes Datenarchiv prüfen
        using var tmp = new TempDir();
        var newest = BackupNames.For(new DateTime(2026, 10, 4, 3, 0, 0)) + ".enc";
        await hub.CloudBackupTarget().DownloadAsync(newest, tmp.File("x.zip"), default);
        await using var zip = File.OpenRead(tmp.File("x.zip"));
        Assert.Equal("backup", DataArchive.ExtractAndVerify(zip, tmp.File("x")).Source);
        // Passwörter nie im Klartext in den Einstellungen
        Assert.DoesNotContain("mein-sicherungs-passwort", File.ReadAllText(dir.File("config.json")));
    }
}

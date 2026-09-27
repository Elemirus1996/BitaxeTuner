using System.Reflection;

namespace BitaxeTuner.Server;

/// <summary>
/// Liefert die in die DLL eingebettete Browser-Oberfläche aus (wwwroot/*). Keine losen Dateien:
/// Oberfläche und API haben immer denselben Stand, auch nach einem Update.
/// </summary>
public static class StaticUi
{
    private static readonly Assembly Assembly = typeof(StaticUi).Assembly;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
    };

    public static void Map(WebApplication app)
    {
        // Aus dem Inhalt der Oberfläche (Assembly.Location ist bei Single-File-Paketen leer)
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var name in Assembly.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)).Order())
        {
            using var s = Assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            sha.AppendData(ms.ToArray());
        }
        var etag = $"\"{Api.Endpoints.Version}-{Convert.ToHexString(sha.GetHashAndReset())[..16]}\"";
        app.MapGet("/{**path}", (string? path, HttpContext http) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path;
            if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            // Unbekannte Pfade ohne Dateiendung: Einstiegsseite (Navigation per #-Anker)
            if (!Path.HasExtension(path)) path = "index.html";
            using var stream = Assembly.GetManifestResourceStream("wwwroot/" + path.Replace('\\', '/'));
            if (stream is null) return Results.NotFound();
            if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(ms.ToArray(), Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"));
        });
    }
}

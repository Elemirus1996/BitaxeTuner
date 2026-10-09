using BitaxeTuner.Core.Config;
using BitaxeTuner.Core.I18n;
using BitaxeTuner.Core.Monitoring;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// 0.9.12 Push direkt im Browser bzw. in der installierten App: Jede Anmeldung eines Geräts wird ein eigenes Push-Ziel
/// („webpush“) mit denselben Bereichen und Miner-Filtern wie ntfy, Telegram usw. Der VAPID-Schlüssel liegt in secrets.json.
/// </summary>
public sealed partial class MinerHub
{
    public const string VapidSecret = "webpush.vapid";
    public const int MaxWebPushTargets = 20;

    private void InitWebPush()
    {
        Notify.VapidKey = () => Secrets.Get(VapidSecret);
        Notify.WebPushGone += id =>
        {
            if (Config.Notifications.Targets.FirstOrDefault(t => t.Id == id) is not { Enabled: true } target) return;
            target.Enabled = false;
            Config.Save();
            LogEvent(null, EventCategories.Settings, L.T("Push an „{0}“ abgeschaltet: Der Browser hat die Anmeldung beendet.", target.Title));
        };
    }

    /// <summary>Öffentlicher VAPID-Schlüssel für den Browser („applicationServerKey“); legt den Schlüssel beim ersten Mal an.</summary>
    public string WebPushPublicKey()
    {
        var pem = Secrets.Get(VapidSecret);
        if (pem is null)
        {
            pem = WebPush.NewVapidKey();
            Secrets.Set(VapidSecret, pem);
        }
        return WebPush.PublicKey(pem);
    }

    /// <summary>
    /// Gerät für Push anmelden. Dieselbe Push-Adresse (z. B. erneut angemeldet) ersetzt nur die Schlüssel und schaltet das
    /// Ziel wieder ein – Bereiche und Miner-Filter bleiben.
    /// </summary>
    public PushTarget AddWebPushTarget(string? endpoint, string? p256dh, string? auth, string? name)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || endpoint!.Length > 1000)
            throw new LocalizedException("Ungültige Push-Adresse des Browsers.");
        byte[] key, secret;
        try
        {
            key = WebPush.FromB64(p256dh ?? "");
            secret = WebPush.FromB64(auth ?? "");
        }
        catch (FormatException)
        {
            throw new LocalizedException("Ungültiger Schlüssel des Browsers.");
        }
        if (key.Length != 65 || key[0] != 4 || secret.Length != 16) throw new LocalizedException("Ungültiger Schlüssel des Browsers.");
        WebPushPublicKey();   // Schlüssel sicher vorhanden

        var targets = Config.Notifications.Targets;
        // Älteres Einzelziel (vor 0.8) in die Liste übernehmen, sonst würde es durch das neue Ziel verdrängt
        if (targets.Count == 0) targets.AddRange(Config.Notifications.EffectiveTargets().Select(t => t.Clone()));
        var target = targets.FirstOrDefault(t => t.Provider == "webpush" && t.WebPushEndpoint == endpoint);
        var isNew = target is null;
        if (target is null)
        {
            if (targets.Count(t => t.Provider == "webpush") >= MaxWebPushTargets)
                throw new LocalizedException("Höchstens {0} Geräte für Push.", MaxWebPushTargets);
            target = new PushTarget { Provider = "webpush", Name = Clip(name, L.T("Browser")) };
            targets.Add(target);
        }
        target.WebPushEndpoint = endpoint;
        target.WebPushP256dh = p256dh!.Trim();
        target.WebPushAuth = auth!.Trim();
        target.Enabled = true;
        Config.Save();
        LogEvent(null, EventCategories.Settings, isNew
            ? L.T("Gerät „{0}“ für Push im Browser angemeldet.", target.Title)
            : L.T("Gerät „{0}“ für Push erneut angemeldet.", target.Title));
        return target;
    }

    private static string Clip(string? s, string fallback)
    {
        var t = (s ?? "").Trim();
        return t.Length == 0 ? fallback : t[..Math.Min(40, t.Length)];
    }
}

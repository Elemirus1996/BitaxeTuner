using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Help;

/// <summary>Ein Fachbegriff: Kennung (für „?“ neben Werten), Begriff und Erklärung in einem Satz.</summary>
public sealed record GlossaryEntry(string Key, string Term, string Text);

/// <summary>
/// 0.9.23 Zentrales Glossar: dieselben Erklärungen hinter dem „?“ im Browser und in der Desktop-App und als Abschnitt
/// „Fachbegriffe“ in der Hilfe. Texte deutsch, übersetzt wie alle anderen (6 Sprachen).
/// </summary>
public static class Glossary
{
    private static readonly (string Key, string Term, string Text)[] Raw =
    [
        ("hashrate", L.N("Hashrate (GH/s, TH/s)"),
            L.N("Wie viele Rechenversuche der Miner pro Sekunde schafft; 1 TH/s = 1000 GH/s = eine Billion Versuche pro Sekunde.")),
        ("efficiency", L.N("Effizienz (J/TH)"),
            L.N("Energie je Terahash: Leistung in Watt geteilt durch die Hashrate in TH/s. Kleiner ist besser – derselbe Ertrag mit weniger Strom.")),
        ("difficulty", L.N("Difficulty (Netzwerk)"),
            L.N("Wie schwer es ist, einen Block zu finden. Das Bitcoin-Netz passt sie etwa alle zwei Wochen an die gesamte Rechenleistung an.")),
        ("bestdiff", L.N("Best Diff"),
            L.N("Die höchste Schwierigkeit, die ein einzelner Share deines Miners je erreicht hat. Erreicht ein Share die Netzwerk-Difficulty, ist es ein Block.")),
        ("shares", L.N("Shares"),
            L.N("Teillösungen, die der Miner beim Pool abgibt. Sie belegen deine Arbeit; danach berechnet der Pool deinen Anteil.")),
        ("rejected", L.N("Abgelehnte Shares / Fehlerrate"),
            L.N("Shares, die der Pool nicht annimmt (zu spät oder ungültig), bzw. Rechenfehler des Chips. Wenige Prozent sind normal, deutlich mehr deutet auf Verbindungs- oder Einstellungsprobleme.")),
        ("pooldiff", L.N("Pool-Difficulty"),
            L.N("Mindestschwierigkeit, die ein Share haben muss, damit der Pool ihn zählt. Höher heißt: weniger, aber gewichtigere Shares.")),
        ("pplns", L.N("PPLNS"),
            L.N("„Pay Per Last N Shares“: Der Pool zahlt, wenn er einen Block findet, verteilt nach deinen Shares der letzten Zeit – schwankt mit dem Glück des Pools.")),
        ("pps", L.N("PPS / FPPS"),
            L.N("„Pay Per Share“: Der Pool zahlt für jeden Share einen festen Betrag – gleichmäßig, dafür mit höherer Gebühr. FPPS gibt zusätzlich Transaktionsgebühren anteilig weiter.")),
        ("solo", L.N("Solo-Mining"),
            L.N("Du suchst allein nach einem Block. Findest du einen, gehört dir die ganze Belohnung – mit kleinen Minern ist die Chance aber sehr gering, eher eine Lotterie.")),
        ("asictemp", L.N("ASIC-Temperatur"),
            L.N("Temperatur des Mining-Chips. Die dauerhaft verträgliche Grenze steht im Geräteprofil (bei Bitaxe meist um 65–70 °C).")),
        ("vrtemp", L.N("VR-Temperatur"),
            L.N("Temperatur der Spannungswandler (Voltage Regulator) auf der Platine. Sie werden bei hoher Kernspannung heiß und brauchen eigene Kühlung.")),
        ("stratum", L.N("Stratum"),
            L.N("Das Protokoll, über das der Miner mit dem Pool spricht; Pool-Adressen sehen aus wie stratum+tcp://pool:Port.")),
    ];

    /// <summary>Alle Begriffe; <paramref name="translate"/> = Sprache der Anfrage (Standard: Programmsprache).</summary>
    public static IReadOnlyList<GlossaryEntry> All(Func<string, string>? translate = null)
    {
        translate ??= s => L.T(s);
        return Raw.Select(r => new GlossaryEntry(r.Key, translate(r.Term), translate(r.Text))).ToList();
    }

    public static GlossaryEntry? Get(string key) => All().FirstOrDefault(e => e.Key == key);
}

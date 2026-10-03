using System.Globalization;
using System.Text;
using BitaxeTuner.Core.Host;
using BitaxeTuner.Core.Plugs;

namespace BitaxeTuner.Core.Monitoring;

/// <summary>
/// Messwerte im Prometheus-Textformat (Version 0.0.4) für eigene Grafana-Dashboards. Bewusst ohne IP-Adressen,
/// Wallet-Adressen, Pool-Benutzer und Smart-Plug-Adressen: Miner werden über Namen und eine nicht sprechende Kennung
/// unterschieden (dieselbe wie in den URLs der Browser-Oberfläche).
/// </summary>
public static class PrometheusMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <param name="deviceId">Kennung eines Miners (Server: <c>Dto.DeviceId</c>), damit Umbenennen keine neue Zeitreihe ergibt.</param>
    public static string Build(MinerHub hub, string version, Func<string, string> deviceId, DateTime now)
    {
        var w = new Writer();
        w.Gauge("bitaxetuner_info", "BitaxeTuner-Version", [("version", version)], 1);

        var devices = hub.Devices;
        var minerLabels = devices.ToDictionary(d => d, d => new[] { ("miner", d.Title), ("id", deviceId(d.Host)) });
        w.Family("bitaxetuner_miner_up", "gauge", "1 = Miner antwortet, 0 = offline");
        foreach (var d in devices) w.Sample("bitaxetuner_miner_up", Model(minerLabels[d], d), d.State.Online && d.Info is not null ? 1 : 0);

        var online = devices.Where(d => d.State.Online && d.Info is not null).ToList();
        void PerMiner(string name, string type, string help, Func<Api.MinerInfo, double?> value)
        {
            var rows = online.Select(d => (d, v: value(d.Info!))).Where(x => x.v is { } v && double.IsFinite(v)).ToList();
            if (rows.Count == 0) return;
            w.Family(name, type, help);
            foreach (var (d, v) in rows) w.Sample(name, minerLabels[d], v!.Value);
        }
        PerMiner("bitaxetuner_miner_hashrate_ghs", "gauge", "Hashrate in GH/s", i => i.HashRateGh);
        PerMiner("bitaxetuner_miner_expected_hashrate_ghs", "gauge", "Von der Firmware erwartete Hashrate in GH/s", i => i.ExpectedHashRateGh);
        PerMiner("bitaxetuner_miner_power_watts", "gauge", "Leistung laut Miner in W", i => i.PowerW);
        PerMiner("bitaxetuner_miner_efficiency_jth", "gauge", "Effizienz in J/TH", i => i.EfficiencyJth);
        PerMiner("bitaxetuner_miner_asic_temperature_celsius", "gauge", "Höchste Chiptemperatur in °C", i => i.MaxChipTempC);
        PerMiner("bitaxetuner_miner_vr_temperature_celsius", "gauge", "Temperatur des Spannungsreglers in °C", i => i.VrTempC);
        PerMiner("bitaxetuner_miner_frequency_mhz", "gauge", "Eingestellte Frequenz in MHz", i => i.FrequencyMhz);
        PerMiner("bitaxetuner_miner_core_voltage_mv", "gauge", "Eingestellte Kernspannung in mV", i => i.CoreVoltageMv);
        PerMiner("bitaxetuner_miner_input_voltage_volts", "gauge", "Eingangsspannung in V", i => i.InputVoltageMv / 1000.0);
        PerMiner("bitaxetuner_miner_fan_percent", "gauge", "Lüfter des Miners in %", i => i.FanPercent);
        PerMiner("bitaxetuner_miner_fan_rpm", "gauge", "Lüfterdrehzahl des Miners in U/min", i => i.FanRpm);
        PerMiner("bitaxetuner_miner_error_percent", "gauge", "Fehlerrate der Hashes in %", i => i.ErrorPercent);
        PerMiner("bitaxetuner_miner_shares_accepted_total", "counter", "Angenommene Shares seit dem Start des Miners", i => i.SharesAccepted);
        PerMiner("bitaxetuner_miner_shares_rejected_total", "counter", "Abgelehnte Shares seit dem Start des Miners", i => i.SharesRejected);
        PerMiner("bitaxetuner_miner_best_difficulty", "gauge", "Beste Difficulty laut Miner", i => i.Details?.bestDiff is { } b && Difficulty.Parse(b) is > 0 and var x ? x : null);
        PerMiner("bitaxetuner_miner_pool_difficulty", "gauge", "Vom Pool vorgegebene Share-Difficulty", i => i.PoolDifficulty);
        PerMiner("bitaxetuner_miner_uptime_seconds", "gauge", "Laufzeit des Miners in s", i => i.UptimeSeconds);

        // Summen über alle Miner, Kosten und Smart Plugs
        var hash = online.Sum(d => d.Info!.HashRateGh);
        var power = online.Sum(d => d.Info!.PowerW);
        var energy = hub.Config.Plugs.Items.Count > 0 ? hub.CurrentEnergy() : null;
        var costPower = energy is { FromPlugs: true } ? energy.TotalPowerW : power;
        w.Gauge("bitaxetuner_miners_online", "Miner online", [], online.Count);
        w.Gauge("bitaxetuner_miners_total", "Eingetragene Miner", [], devices.Count);
        w.Gauge("bitaxetuner_hashrate_ghs", "Gesamt-Hashrate in GH/s", [], hash);
        w.Gauge("bitaxetuner_power_watts", "Gesamtleistung laut Miner in W", [], power);
        if (energy is { FromPlugs: true })
            w.Gauge("bitaxetuner_wall_power_watts", "Gesamtleistung an der Steckdose in W", [], energy.TotalPowerW);
        var price = hub.Prices.PriceAt(now.ToUniversalTime());
        var ct = EnergyCost.CurrentCt(hub.Config, price);
        w.Gauge("bitaxetuner_electricity_price_ct_per_kwh", "Strompreis in ct/kWh (fest oder aus der Preisquelle)", [], ct);
        w.Gauge("bitaxetuner_cost_per_day", "Stromkosten pro Tag beim aktuellen Verbrauch", [("currency", hub.Config.Currency)], costPower * 24 / 1000.0 * ct / 100.0);

        var plugs = hub.Config.Plugs.Items.Count > 0 ? hub.PlugStatuses() : [];
        if (plugs.Count > 0)
        {
            (string, string)[] L(PlugStatus p) => [("plug", p.Name), ("role", p.Role)];
            w.Family("bitaxetuner_plug_up", "gauge", "1 = Smart Plug antwortet");
            foreach (var p in plugs) w.Sample("bitaxetuner_plug_up", L(p), p.Online ? 1 : 0);
            Plug("bitaxetuner_plug_power_watts", "gauge", "Leistung an der Steckdose in W", p => p.PowerW);
            Plug("bitaxetuner_plug_energy_kwh_total", "counter", "Energiezähler des Plugs in kWh", p => p.EnergyWh / 1000.0);
            Plug("bitaxetuner_plug_voltage_volts", "gauge", "Netzspannung in V", p => p.Voltage);
            void Plug(string name, string type, string help, Func<PlugStatus, double?> value)
            {
                var rows = plugs.Where(p => p.Online && value(p) is { } v && double.IsFinite(v)).ToList();
                if (rows.Count == 0) return;
                w.Family(name, type, help);
                foreach (var p in rows) w.Sample(name, L(p), value(p)!.Value);
            }
        }

        // Zusatzlüfter (Pico) und Temperaturfühler
        var fans = hub.FanStatus;
        if (fans.Enabled)
        {
            var channels = fans.Channels.Where(c => c.Role != "none").ToList();
            if (channels.Count > 0)
            {
                (string, string)[] L(FanChannelStatus c) => [("channel", c.Channel.ToString(CultureInfo.InvariantCulture)), ("name", c.Name), ("role", c.Role)];
                w.Family("bitaxetuner_fan_percent", "gauge", "Zusatzlüfter: Sollwert in %");
                foreach (var c in channels) w.Sample("bitaxetuner_fan_percent", L(c), c.Percent);
                var rpm = channels.Where(c => c.Rpm is not null).ToList();
                if (rpm.Count > 0)
                {
                    w.Family("bitaxetuner_fan_rpm", "gauge", "Zusatzlüfter: Drehzahl in U/min");
                    foreach (var c in rpm) w.Sample("bitaxetuner_fan_rpm", L(c), c.Rpm!.Value);
                }
            }
            var sensors = (fans.Sensors ?? []).Where(s => s.Temp is not null).ToList();
            if (sensors.Count > 0)
            {
                w.Family("bitaxetuner_sensor_temperature_celsius", "gauge", "Temperaturfühler in °C");
                foreach (var s in sensors) w.Sample("bitaxetuner_sensor_temperature_celsius", [("sensor", s.Name.Length > 0 ? s.Name : s.Id)], s.Temp!.Value);
            }
        }
        return w.ToString();
    }

    private static (string, string)[] Model((string, string)[] labels, HubDevice d) =>
        (d.Info?.DeviceModel ?? d.Info?.AsicModel) is { Length: > 0 } m ? [.. labels, ("model", m)] : labels;

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        public void Family(string name, string type, string help) =>
            _sb.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n')
               .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

        public void Gauge(string name, string help, (string, string)[] labels, double value)
        {
            Family(name, "gauge", help);
            Sample(name, labels, value);
        }

        public void Sample(string name, (string Key, string Value)[] labels, double value)
        {
            _sb.Append(name);
            if (labels.Length > 0)
                _sb.Append('{').Append(string.Join(",", labels.Select(l => $"{l.Key}=\"{Escape(l.Value)}\""))).Append('}');
            _sb.Append(' ').Append(Format(value)).Append('\n');
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        private static string Format(double v) =>
            double.IsNaN(v) ? "NaN" : double.IsPositiveInfinity(v) ? "+Inf" : double.IsNegativeInfinity(v) ? "-Inf"
            : Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        public override string ToString() => _sb.ToString();
    }
}

using System.Net.Http;
using System.Text.Json;
using BitaxeTuner.Core.Tax.Models;
using BitaxeTuner.Core.Tax.Services;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Network;

public sealed class WalletInfo
{
    public string Address { get; set; } = "";
    public long BalanceSat { get; set; }
    public long UnconfirmedSat { get; set; }
    public int TxCount { get; set; }

    public long? LastIncomingSat { get; set; }
    public DateTime? LastIncomingTime { get; set; }
    public string? LastIncomingTxid { get; set; }
    public bool LastIncomingConfirmed { get; set; }
}

/// <summary>
/// Guthaben und letzter Eingang einer Adresse. BTC über mempool.space,
/// BCH (CashAddr oder bitcoincash:-Präfix) über Blockchair. Legacy-Adressen
/// (1…/3…) gelten hier als BTC; für BCH-Legacy-Adressen das Steuer-Modul nutzen,
/// dort ist der Coin explizit.
/// </summary>
public sealed class WalletClient : IDisposable
{
    private const string Base = "https://mempool.space/api";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private BlockchairBlockchainService? _blockchair;

    public WalletClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BitaxeMonitor/1.0");
    }

    /// <summary>Blockchair-Client des Steuer-Moduls mitbenutzen (gemeinsames Rate-Limit).</summary>
    public void UseBlockchairForBch(BlockchairBlockchainService blockchair) => _blockchair = blockchair;

    public static bool IsBitcoinCash(string address)
        => CoinTypeExtensions.IsUnambiguous(address)
           && CoinTypeExtensions.GuessFromAddress(address) == CoinType.BitcoinCash;

    public Task<WalletInfo> GetAsync(string address, CancellationToken ct)
        => IsBitcoinCash(address) && _blockchair is not null
            ? GetBchAsync(address, ct)
            : GetBtcAsync(address, ct);

    private async Task<WalletInfo> GetBchAsync(string address, CancellationToken ct)
    {
        var info = new WalletInfo { Address = address.Trim() };

        // Ein Abruf für Guthaben und Eingänge, geteilt mit dem Steuer-Monitor (Cache)
        var snapshot = await _blockchair!.GetAddressAsync(address, CoinType.BitcoinCash, ct).ConfigureAwait(false);
        info.BalanceSat = snapshot.BalanceSat;
        info.TxCount = snapshot.TxCount;

        var last = snapshot.Incoming.OrderByDescending(t => t.ReceivedAtUtc).FirstOrDefault();
        if (last is not null)
        {
            info.LastIncomingSat = (long)(last.Amount * 100_000_000m);
            info.LastIncomingTxid = last.TxId;
            info.LastIncomingConfirmed = true;
            info.LastIncomingTime = last.ReceivedAtUtc.ToLocalTime();
        }

        return info;
    }

    private async Task<WalletInfo> GetBtcAsync(string address, CancellationToken ct)
    {
        address = address.Trim();
        var info = new WalletInfo { Address = address };

        // Guthaben
        using (var resp = await MempoolLimit.GetAsync(_http, $"{Base}/address/{address}", ct).ConfigureAwait(false))
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new InvalidOperationException(L.T("Adresse ungültig"));
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            var chain = root.GetProperty("chain_stats");
            var mem = root.GetProperty("mempool_stats");

            info.BalanceSat = chain.GetProperty("funded_txo_sum").GetInt64()
                            - chain.GetProperty("spent_txo_sum").GetInt64();
            info.UnconfirmedSat = mem.GetProperty("funded_txo_sum").GetInt64()
                                - mem.GetProperty("spent_txo_sum").GetInt64();
            info.TxCount = chain.GetProperty("tx_count").GetInt32() + mem.GetProperty("tx_count").GetInt32();
        }

        if (info.TxCount == 0) return info;

        // Letzte eingehende Transaktion suchen
        using (var resp = await MempoolLimit.GetAsync(_http, $"{Base}/address/{address}/txs", ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            foreach (var tx in doc.RootElement.EnumerateArray())
            {
                long received = 0;
                foreach (var vout in tx.GetProperty("vout").EnumerateArray())
                {
                    if (vout.TryGetProperty("scriptpubkey_address", out var a) &&
                        string.Equals(a.GetString(), address, StringComparison.OrdinalIgnoreCase))
                    {
                        received += vout.GetProperty("value").GetInt64();
                    }
                }

                if (received <= 0) continue;

                info.LastIncomingSat = received;
                info.LastIncomingTxid = tx.GetProperty("txid").GetString();

                var status = tx.GetProperty("status");
                info.LastIncomingConfirmed = status.GetProperty("confirmed").GetBoolean();
                if (info.LastIncomingConfirmed && status.TryGetProperty("block_time", out var bt))
                    info.LastIncomingTime = DateTimeOffset.FromUnixTimeSeconds(bt.GetInt64()).LocalDateTime;

                break; // Liste ist absteigend sortiert (Mempool zuerst, dann neueste Blöcke)
            }
        }

        return info;
    }

    public void Dispose() => _http.Dispose();
}

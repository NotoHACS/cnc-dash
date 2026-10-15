using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CncDash.Collector;

public class MtConnectClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // streams namespace: agents differ per build/version — cnc12 speaks 1.4,
    // CNC13's old agent is 1.3. Resolve from the RESPONSE root, not a const:
    // a hardcoded 1.4 ns made every Header lookup miss on 1.3 agents (no
    // nextSequence -> infinite "seeding from /current" loop).
    private static XNamespace StreamsNs(XDocument doc) =>
        doc.Root?.Name.Namespace ?? XNamespace.None;

    private readonly HttpClient http;
    private readonly CollectorConfig cfg;
    private readonly Diagnostics? diag;
    private bool cvLoggedOnce;

    public MtConnectClient(CollectorConfig cfg)
    {
        this.cfg = cfg;
        diag = cfg.DiagnosticDumpSeconds > 0 ? new Diagnostics(cfg) : null;
        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "Accept", "application/xml" } },
        };
        http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <summary>Temporary per-poll data-item observer; null when diagnostics are off.</summary>
    public Diagnostics? Diagnostics => diag;

    /// <summary>
    /// Poll the agent: /sample replay streaming (UseStreaming=1, default) or
    /// legacy /current polling (flip via config_local.json without a redeploy).
    /// </summary>
    public async Task<PollResult?> PollAsync(Dictionary<string, string> lastValues)
    {
        if (cfg.UseStreaming == 1)
            return await SampleStreamAsync(lastValues);
        return await CurrentSnapshotAsync(lastValues);
    }

    /// <summary>
    /// /current snapshot: change-detected values + triggers; also used to seed
    /// or reseed the streaming cursor (lastValues cleared = full snapshot posts).
    /// </summary>
    public async Task<PollResult?> CurrentSnapshotAsync(Dictionary<string, string> lastValues)
    {
        string? xml = null;
        try
        {
            var uri = $"{cfg.AgentUrl.TrimEnd('/')}/{cfg.DevicePath.Trim('/')}/current";
            xml = await http.GetStringAsync(uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] poll failed: {ex.Message}");
            return null;
        }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] xml parse failed: {ex.Message}");
            return null;
        }

        var result = new PollResult
        {
            Samples = new List<SampleRow>(),
            AssetChangedTrigger = false,
        };

        // agent bookkeeping (MTConnectStreams Header): instanceId identifies the
        // agent's in-memory instance; first/nextSequence bound the /sample replay
        // window (persisted for the future streamer; NOT used to vet /current
        // item sequences — those may legitimately sit below firstSequence). An
        // instanceId change means the cached change-detection state is stale —
        // clear it so this poll re-posts a full snapshot.
        lastValues.TryGetValue("instanceId", out var prevInstStr);
        ulong.TryParse(prevInstStr, out var prevInst);
        ulong firstSeq = 0, nextSeq = 0;
        string? instAttr = null;
        var header = doc.Root?.Element(StreamsNs(doc) + "Header");
        if (header == null)
        {
            // 1.3 agents sometimes nest Header differently or the root isn't
            // MTConnectStreams — last resort: any Header element anywhere
            header = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Header");
        }
        if (header != null)
        {
            instAttr = header.Attribute("instanceId")?.Value;
            ulong.TryParse(instAttr, out var curInst);
            ulong.TryParse(header.Attribute("firstSequence")?.Value, out firstSeq);
            ulong.TryParse(header.Attribute("nextSequence")?.Value, out nextSeq);

            if (curInst > 0 && prevInst > 0 && curInst != prevInst)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] agent restart detected (instanceId {prevInst} -> {curInst}) - resnapshotting");
                lastValues.Clear();
            }
        }

        var elements = doc.Descendants()
            .Where(e => e.Attribute("dataItemId") != null);

        foreach (var e in elements)
        {
            var di = e.Attribute("dataItemId")!.Value;
            var tsAttr = e.Attribute("timestamp")?.Value;
            var ts = tsAttr != null ? DateTime.Parse(tsAttr).ToUniversalTime() : DateTime.UtcNow;
            diag?.Observe(di, e.Value, tsAttr ?? "", e.Attribute("sequence")?.Value ?? "");

            var isAssetTrigger = cfg.AssetTriggerItems.Any(t =>
                di.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (isAssetTrigger)
            {
                // the agent re-emits the CURRENT tool's assetId every poll;
                // change-detect the value like any other item so a targeted
                // fetch fires only on an actual tool/asset change (the full
                // refetch still happens on the AssetSeconds cadence)
                HandleAssetTrigger(di, e, lastValues, result);
                continue;
            }

            var sample = SampleRow.From(di, ts, 0, e);
            if (sample is null) continue;

            // sequence: agent-provided when present; treat it as-is. /current
            // semantics: an item's sequence marks WHEN THAT VALUE WAS SET and
            // may legitimately sit below the agent's sliding buffer window
            // (values captured long ago) — do NOT resnapshot on per-item
            // membership; agent restarts are signaled by the Header instanceId
            // (handled above the loop). Okuma extension elements
            // (e.g. Mp1CommonVariable, subType x:COMMON) may omit the
            // attribute entirely — fall back to a stable hash of the payload
            // so the server's (di, seq) dedupe key still changes when the
            // value does.
            var seqStr = e.Attribute("sequence")?.Value;
            if (seqStr != null && long.TryParse(seqStr, out var agentSeq))
            {
                sample.seq = agentSeq;
            }
            else
            {
                sample.seq = HashPayload(sample.ValueCanonical());
            }

            // canonical value = only the payload fields we care about
            var value = sample.ValueCanonical();
            if (lastValues.TryGetValue(di, out var prev) && prev == value)
                continue;

            lastValues[di] = value;
            result.Samples.Add(sample);
            LogFirstCvPost(sample, di);
        }

        // persist agent bookkeeping alongside the last values (state.json) —
        // refreshed from THIS response's Header on every successful poll
        if (instAttr != null && ulong.TryParse(instAttr, out var instOut))
            lastValues["instanceId"] = instOut.ToString();
        if (firstSeq > 0)
            lastValues["firstSequence"] = firstSeq.ToString();
        if (nextSeq > 0)
            lastValues["nextSequence"] = nextSeq.ToString();

        // Execution/mode context travels on the sample rows that carry them
        // (server merges by timestamp), so events map onto: exec + mode + fmode.
        return result;
    }

    /// <summary>
    /// /sample-backed poll: requests events since the last known sequence
    /// (replay), advances the cursor from the response Header (or the last
    /// parsed event when the response saturated the count), and reseeds from
    /// /current on instance change, error documents, or a cursor older than
    /// the agent's buffer window.
    /// </summary>
    public async Task<PollResult?> SampleStreamAsync(Dictionary<string, string> lastValues)
    {
        ulong.TryParse(lastValues.TryGetValue("nextSequence", out var fromStr) ? fromStr : "", out var from);

        // no known cursor (fresh state): seed from a /current snapshot
        if (from == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] no sequence state yet; seeding from /current");
            return await CurrentSnapshotAsync(lastValues);
        }

        ulong firstSeq = 0, nextSeq = 0;
        string? instAttr = null;
        string? xml;

        // network/HTTP errors: never advance the cursor; retry next interval.
        // 4xx statuses (cursor older than the agent buffer, etc.) reseed once.
        try
        {
            var uri = $"{cfg.AgentUrl.TrimEnd('/')}/{cfg.DevicePath.Trim('/')}/sample?from={from}&count={Math.Max(1, cfg.SampleCount)}";
            using (var resp = await http.GetAsync(uri))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sample fetch returned HTTP {(int)resp.StatusCode} for from={from} - resnapshotting");
                    return await ReseedFromCurrentAsync(lastValues);
                }
                xml = await resp.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sample fetch failed (from={from}): {ex.Message}");
            return null;
        }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] xml parse failed (sample): {ex.Message}");
            return null;
        }

        // 200 + MTConnectError document (e.g. OUT_OF_RANGE)
        if (doc.Root?.Name.LocalName == "MTConnectErrors")
        {
            var code = doc.Root.Descendants().FirstOrDefault()?.Attribute("errorCode")?.Value ?? "unknown";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sample request error ({code}) for from={from} - resnapshotting");
            return await ReseedFromCurrentAsync(lastValues);
        }

        // header: instance change (agent restart) or cursor below the buffer
        // window both invalidate streaming state — reseed from /current
        var header = doc.Root?.Element(StreamsNs(doc) + "Header");
        if (header == null)
            header = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Header");
        if (header != null)
        {
            instAttr = header.Attribute("instanceId")?.Value;
            ulong.TryParse(instAttr, out var curInst);
            ulong.TryParse(header.Attribute("firstSequence")?.Value, out firstSeq);
            ulong.TryParse(header.Attribute("nextSequence")?.Value, out nextSeq);

            lastValues.TryGetValue("instanceId", out var prevInstStr);
            ulong.TryParse(prevInstStr, out var prevInst);

            if (curInst > 0 && prevInst > 0 && curInst != prevInst)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] agent restart detected (instanceId {prevInst} -> {curInst}) - resnapshotting");
                return await ReseedFromCurrentAsync(lastValues);
            }
            if (firstSeq > 0 && from < firstSeq)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sequence {from} older than agent buffer [{firstSeq}, {nextSeq}) - resnapshotting");
                return await ReseedFromCurrentAsync(lastValues);
            }
        }

        // parse events (ordered by sequence; sequence REQUIRED in sample mode —
        // /sample events carry real agent sequences; attr-less elements are a
        // protocol violation here: log once per poll and skip)
        var result = new PollResult
        {
            Samples = new List<SampleRow>(),
            AssetChangedTrigger = false,
        };
        ulong maxSeq = 0;
        ulong eventCount = 0;
        var countCap = (ulong)Math.Max(1, cfg.SampleCount);
        var seqWarned = false;

        var elements = doc.Descendants()
            .Where(e => e.Attribute("dataItemId") != null);

        foreach (var e in elements)
        {
            var di = e.Attribute("dataItemId")!.Value;
            var tsAttr = e.Attribute("timestamp")?.Value;
            var ts = tsAttr != null ? DateTime.Parse(tsAttr).ToUniversalTime() : DateTime.UtcNow;
            var seqStr = e.Attribute("sequence")?.Value;
            diag?.Observe(di, e.Value, tsAttr ?? "", seqStr ?? "");

            if (seqStr == null || !long.TryParse(seqStr, out var agentSeq))
            {
                if (!seqWarned)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] sample event without sequence skipped: {di}");
                    seqWarned = true;
                }
                continue;
            }
            var seqU = (ulong)agentSeq;
            if (seqU > maxSeq) maxSeq = seqU;
            eventCount++;

            if (cfg.AssetTriggerItems.Any(t => di.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                HandleAssetTrigger(di, e, lastValues, result);
                continue;
            }

            var sample = SampleRow.From(di, ts, agentSeq, e);
            if (sample is null) continue;

            // safety net: /sample only returns changed events by spec; the
            // canonical gate also dedupes cursor replays (from is inclusive)
            var value = sample.ValueCanonical();
            if (lastValues.TryGetValue(di, out var prev) && prev == value)
                continue;

            lastValues[di] = value;
            result.Samples.Add(sample);
            LogFirstCvPost(sample, di);
        }

        // cursor bookkeeping: with a saturated response, hold at the last parsed
        // sequence so the pending remainder drains next round; otherwise jump to
        // the agent's head (nextSequence = where the next from= starts)
        ulong newSeq;
        if (eventCount >= countCap && maxSeq > 0)
            newSeq = maxSeq;
        else if (nextSeq > 0)
            newSeq = nextSeq;
        else if (maxSeq > 0)
            newSeq = maxSeq;
        else
            newSeq = from;

        lastValues["nextSequence"] = newSeq.ToString();
        if (instAttr != null && ulong.TryParse(instAttr, out var instOut))
            lastValues["instanceId"] = instOut.ToString();
        if (firstSeq > 0)
            lastValues["firstSequence"] = firstSeq.ToString();

        return result;
    }

    /// <summary>
    /// Reseed: drop the change-detection cache (next /current posts a full
    /// snapshot) and advance the cursor from the /current Header.
    /// </summary>
    private async Task<PollResult?> ReseedFromCurrentAsync(Dictionary<string, string> lastValues)
    {
        lastValues.Clear();
        return await CurrentSnapshotAsync(lastValues);
    }

    // the agent re-emits the CURRENT tool's assetId every poll; change-detect
    // the value like any other item so a targeted fetch fires only on an
    // actual tool/asset change (the full refetch still happens on AssetSeconds)
    private void HandleAssetTrigger(string di, XElement e, Dictionary<string, string> lastValues, PollResult result)
    {
        lastValues.TryGetValue(di, out var prevTrig);
        if (prevTrig != e.Value)
        {
            result.AssetChangedTrigger = true;
            result.ChangedAssetId = e.Value;
        }
        lastValues[di] = e.Value;
    }

    // first common-variable post visibility (subsequent change posts are silent)
    private void LogFirstCvPost(SampleRow sample, string di)
    {
        if (!di.Equals("Mp1CommonVariable", StringComparison.OrdinalIgnoreCase) || cvLoggedOnce) return;
        cvLoggedOnce = true;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] common variables posted: stations={sample.stationsOn} tally={sample.partsTally} ({sample.commonVars?.Length ?? 0} chars)");
    }

    // FNV-1a 64 of the payload → stable positive sequence for items whose agent
    // element carries no sequence attribute (server dedupe key still value-bound)
    private static long HashPayload(string value)
    {
        unchecked
        {
            ulong h = 14695981039346656037;
            foreach (var ch in value)
            {
                h ^= ch;
                h *= 1099511628211;
            }
            return (long)(h & 0x7FFFFFFFFFFFFFFF);
        }
    }
}

public class PollResult
{
    public List<SampleRow> Samples { get; set; } = new();
    public bool AssetChangedTrigger { get; set; }

    // assetId from the asset-change trigger (e.g. asset_chg) for targeted fetch
    public string? ChangedAssetId { get; set; }
}

public class SampleRow
{
    public DateTime ts { get; set; }
    public string di { get; set; } = default!;
    public long seq { get; set; }
    public double? speed { get; set; }
    public string? exec { get; set; }
    public string? mode { get; set; }
    public string? fmode { get; set; }
    public long? count { get; set; }
    public string? prog { get; set; }
    public int? tool { get; set; }
    public double? runtime { get; set; }
    public double? totalCuttingTime { get; set; }
    public double? feedCmd { get; set; }
    public double? xAct { get; set; }
    public double? yAct { get; set; }
    public double? zAct { get; set; }
    public long? line { get; set; }
    public string? block { get; set; }
    public string? commonVars { get; set; }
    public int? stationsOn { get; set; }
    public double? partsTally { get; set; }

    public static SampleRow? From(string di, DateTime ts, long seq, XElement e)
    {
        var row = new SampleRow { ts = ts, di = di, seq = seq };

        // dataItemId prefixes vary per agent build: newer Okuma agents use
        // Mp1*/MS1*/MX1* prefixes, older 1.3-style agents (CNC13's) prefix with
        // the machine code ("C13S1speed", "C13X1actm", ...). Match by SUFFIX so
        // both shapes map onto the same columns; the raw di stays per-row.
        // NOTE: value types differ per item (double vs long vs text) and the
        // semantic of e.g. "*S1speed" is stable across agents.
        bool Has(string suffix) => di.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        if (Has("S1speed") || Has("SpindleSpeed")) { if (!double.TryParse(e.Value, out var s)) return null; row.speed = s; }
        else if (Has("partcount")) { if (!long.TryParse(e.Value, out var c)) return null; row.count = c; }
        else if (Has("program")) { row.prog = e.Value; }
        else if (Has("CurrentTool")) { if (!int.TryParse(e.Value, out var t)) return null; row.tool = t; }
        else if (Has("execution")) { row.exec = e.Value; }
        else if (Has("controllermode")) { row.mode = e.Value; }
        else if (di.Equals("Mpmode", StringComparison.OrdinalIgnoreCase)) { row.mode = e.Value; }
        else if (Has("fmode") || di.Equals("Mfmode", StringComparison.OrdinalIgnoreCase)) { row.fmode = e.Value; }
        else if (Has("SpindleRunTime")) { if (!double.TryParse(e.Value, out var r)) return null; row.runtime = r; }
        else if (Has("TotalCuttingTime")) { if (!double.TryParse(e.Value, out var tc)) return null; row.totalCuttingTime = tc; }
        else if (di.Equals("Mp1Fcmd", StringComparison.OrdinalIgnoreCase)) { if (!double.TryParse(e.Value, out var f)) return null; row.feedCmd = f; }
        else if (Has("X1actm")) { if (!double.TryParse(e.Value, out var x)) return null; row.xAct = x; }
        else if (Has("Y1actm")) { if (!double.TryParse(e.Value, out var y)) return null; row.yAct = y; }
        else if (Has("Z1actm")) { if (!double.TryParse(e.Value, out var z)) return null; row.zAct = z; }
        else if (Has("Mp1line") || Has("LineId")) { if (!long.TryParse(e.Value, out var ln)) return null; row.line = ln; }
        else if (Has("block")) { row.block = e.Value; }
        else if (Has("CommonVariable")) { var cv = ParseCommonVars(e.Value); if (cv == null) return null; row.commonVars = cv.Value.map; row.stationsOn = cv.Value.stations; row.partsTally = cv.Value.tally; }
        else return null;
        return row;
    }

    /// <summary>Value used for change detection (payload only, ts/seq excluded).</summary>
    public string ValueCanonical()
    {
        if (speed.HasValue) return $"speed:{speed.Value:0.#}";
        if (exec != null) return $"exec:{exec}";
        if (mode != null) return $"mode:{mode}";
        if (fmode != null) return $"fmode:{fmode}";
        if (count.HasValue) return $"count:{count.Value}";
        if (prog != null) return $"prog:{prog}";
        if (tool.HasValue) return $"tool:{tool.Value}";
        if (runtime.HasValue) return $"runtime:{runtime.Value:0.#}";
        if (totalCuttingTime.HasValue) return $"tcut:{totalCuttingTime.Value:0.#}";
        if (feedCmd.HasValue) return $"feed:{feedCmd.Value:0.#}";
        if (xAct.HasValue) return $"x:{xAct.Value:0.#}";
        if (yAct.HasValue) return $"y:{yAct.Value:0.#}";
        if (zAct.HasValue) return $"z:{zAct.Value:0.#}";
        if (line.HasValue) return $"line:{line.Value}";
        if (block != null) return $"block:{block}";
        if (commonVars != null) return $"cvars:{commonVars}";
        return "";
    }

    /// <summary>
    /// Mp1CommonVariable carries all Okuma common variables as one space-separated
    /// text: "N:value N:value ..." (N = VC number). Values may be '&#x221E;'
    /// (infinity, unused) or otherwise unparsable — those tokens are ignored.
    /// Returns the compact map ("40=3;41=12;65=5733", truncated to 4000 chars)
    /// plus dedicated convenience values for VC40 (stations on) and VC41 (parts tally).
    /// </summary>
    private static (string map, int? stations, double? tally)? ParseCommonVars(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        int? stations = null;
        double? tally = null;

        // whitespace-agnostic: the agent may emit tokens separated by plain
        // spaces, tabs, or newlines
        foreach (var tok in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ci = tok.IndexOf(':');
            if (ci <= 0) continue;
            if (!int.TryParse(tok[..ci], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) continue;
            var rawVal = tok[(ci + 1)..];
            if (!double.TryParse(rawVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(idx).Append('=').Append(val.ToString("0.####", CultureInfo.InvariantCulture));
            if (idx == 40) stations = (int)val;
            if (idx == 41) tally = val;
        }
        if (sb.Length == 0) return null;
        var map = sb.ToString();
        if (map.Length > 4000) map = map[..4000];
        return (map, stations, tally);
    }
}
/// <summary>
/// Temporary instrumentation for finding unmapped data items: observes every
/// dataItem element seen in /current and periodically appends a compact
/// itemId/count/first/last dump to a log file. Off unless DiagnosticDumpSeconds &gt; 0.
/// </summary>
public class Diagnostics
{
    private class Obs
    {
        public int Count;
        public string? FirstValue;
        public string? LastValue;
        public string? LastTs;
    }

    private readonly string path;
    private readonly Dictionary<string, Obs> items = new();

    public string LogPath { get; }

    public Diagnostics(CollectorConfig cfg)
    {
        var p = string.IsNullOrWhiteSpace(cfg.DiagnosticLogPath) ? "datadump.log" : cfg.DiagnosticLogPath!;
        path = Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        LogPath = path;
    }

    public void Observe(string id, string value, string tsRaw, string seqRaw)
    {
        var v = Trunc(value);
        if (!items.TryGetValue(id, out var o))
        {
            items[id] = new Obs { Count = 1, FirstValue = v, LastValue = v, LastTs = tsRaw };
        }
        else
        {
            o.Count = o.Count + 1;
            o.LastValue = v;
            o.LastTs = tsRaw;
        }
    }

    public void Dump()
    {
        if (items.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            foreach (var kv in items.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{kv.Key} count={kv.Value.Count} first='{kv.Value.FirstValue}' last='{kv.Value.LastValue}'");
            }
            File.AppendAllText(path, sb.ToString());
            items.Clear(); // fresh accumulation window: count/first reset each dump
        }
        catch (Exception ex)
        {
            // a locked/missing file must never kill the collector; keep the dict
            // so a later successful dump still covers this window
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] diagnostics dump failed: {ex.Message}");
        }
    }

    private static string Trunc(string v) => v.Length <= 200 ? v : v[..200];
}

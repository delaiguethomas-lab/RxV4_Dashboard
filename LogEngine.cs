// ================================================================
// Moteur d'analyse des logs UV Laser
// Port C# de la partie "donnees" de Logs_Analysis.py :
//  - parsing XML / texte des fichiers .xml .log .txt
//  - deduplication des erreurs (fenetre en secondes)
//  - agregations par site et par jour (lentilles, sante, calibrations,
//    temps de marquage, erreurs)
//  - journal des changements d'offsets
// Aucune dependance a l'interface : peut etre teste seul.
// ================================================================

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LogsAnalysis;

// ---------------------------------------------------------------
// Modeles
// ---------------------------------------------------------------
public sealed class LogEvent
{
    public DateTime? Date { get; set; }
    public int? TimeS { get; set; }
    public string? Msg { get; set; }
    public bool Marked { get; set; }
    public double? MarkingTime { get; set; }
    public bool IsHealth { get; set; }
    public bool? HealthGo { get; set; }
    public List<string> HealthReasons { get; } = new();
    /// <summary>Non nul = instantane des offsets (pas un evenement d'erreur).</summary>
    public Dictionary<string, double>? Offsets { get; set; }
}

public sealed class Calibration
{
    public DateTime? Date { get; set; }
    public int? TimeS { get; set; }
    public double[] Xs { get; set; } = Array.Empty<double>();
    public double[] Ys { get; set; } = Array.Empty<double>();
}

public sealed class OffsetObs
{
    public string Site { get; set; } = "";
    public DateTime Date { get; set; }
    public int? TimeS { get; set; }
    public string File { get; set; } = "";
    public Dictionary<string, double> Offsets { get; set; } = new();
}

public sealed class OffsetChange
{
    public string Site { get; set; } = "";
    public DateTime Date { get; set; }
    public int? TimeS { get; set; }
    public string Param { get; set; } = "";
    public double Old { get; set; }
    public double New { get; set; }
    public double Delta => New - Old;
    public double AbsDelta => Math.Abs(New - Old);
    public string File { get; set; } = "";
}

public sealed class SiteMetrics
{
    public Dictionary<DateTime, int> Lenses { get; } = new();
    public Dictionary<DateTime, Dictionary<string, int>> Errors { get; } = new();
    public Dictionary<DateTime, int> HealthTotal { get; } = new();
    public Dictionary<DateTime, int> HealthOk { get; } = new();
    public Dictionary<DateTime, int> HealthKo { get; } = new();
    public Dictionary<DateTime, Dictionary<string, int>> HealthKoReasons { get; } = new();
    /// <summary>Temps de marquage bruts (en millisecondes) par jour.</summary>
    public Dictionary<DateTime, List<double>> MarkingTimes { get; } = new();
    public Dictionary<DateTime, int> CalibrationsPerDate { get; } = new();
    public Dictionary<DateTime, List<Calibration>> CalibrationsData { get; } = new();

    public IEnumerable<DateTime> AllDates()
    {
        return Lenses.Keys
            .Concat(Errors.Keys)
            .Concat(HealthTotal.Keys)
            .Concat(HealthOk.Keys)
            .Concat(HealthKo.Keys)
            .Concat(CalibrationsPerDate.Keys)
            .Concat(MarkingTimes.Keys);
    }
}

public sealed class LogIndex
{
    public Dictionary<string, SiteMetrics> Sites { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<OffsetObs>> OffsetObservations { get; } = new(StringComparer.Ordinal);
    public List<OffsetChange> OffsetChanges { get; set; } = new();
    public int FilesParsed { get; set; }

    public List<string> SiteNames() => Sites.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public IEnumerable<DateTime> AllDates()
    {
        foreach (var sb in Sites.Values)
            foreach (var d in sb.AllDates())
                yield return d;
        foreach (var c in OffsetChanges)
            yield return c.Date;
    }
}

// ---------------------------------------------------------------
// Moteur
// ---------------------------------------------------------------
public static class LogEngine
{
    public const int DefaultDedupSeconds = 5;
    public const string CalibTag = "NewCalibration";

    public static readonly string[] OffsetKeys = { "OffsetX", "OffsetY", "ScaleX", "ScaleY", "FieldRotation" };

    private static readonly string[] ErrorKeywords =
        { "error", "unable", "disappeared", "failed", "critical", "door opened" };
    private static readonly HashSet<string> ExcludeSet =
        new() { "na", "bypassed by user", "stopping cycle" };

    private static readonly Regex TimeRe = new(@"\b(\d{2}):(\d{2}):(\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex IsoTimeRe = new(@"T(\d{2}):(\d{2}):(\d{2})", RegexOptions.Compiled);
    private static readonly Regex NumListRe = new(
        @"^\s*[+\-]?\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?(?:\s*;\s*[+\-]?\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?)+\s*$",
        RegexOptions.Compiled);
    private static readonly Regex GoRe = new(@"\bGo\s*=\s*(True|False)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatusRe = new(@"\bStatus\s*=\s*([^;\r,\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarkingTimeRe = new(@"\bMarkingTime\s*=\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OffsetLineRe = new(
        @"(?:^|;)\s*(OffsetX|OffsetY|ScaleX|ScaleY|FieldRotation)\s*=\s*([\-\+]?\d+(?:\.\d+)?)\s*(?=;|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlainNumRe = new(@"^\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex TagWrapRe = new(
        @"^\s*<([A-Za-z0-9_\-:\.]+)(\s[^>]*)?>\s*(.*)?\s*</\1>\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnyTagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);

    // ============================ Dates ============================
    private static readonly string[] DmyFormats = { "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d_M_yyyy" };
    private static readonly string[] YmdFormats = { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy_M_d" };

    private static bool TryExact(string s, string[] formats, out DateTime dt)
    {
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
            && dt.Year >= 1990)
            return true;
        dt = default;
        return false;
    }

    /// <summary>Accepte jj/mm/aaaa, jj-mm-aaaa, aaaa-mm-jj, ... (avec ou sans "Thh:mm:ss").</summary>
    public static DateTime? NormalizeAnyDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (TryExact(s, DmyFormats, out var dt)) return dt.Date;
        var basePart = s.Split('T')[0];
        if (TryExact(basePart, YmdFormats, out dt)) return dt.Date;
        return null;
    }

    private static DateTime? NormalizeDateAttr(string? attr, DateTime? fallback)
        => NormalizeAnyDate(attr) ?? fallback;

    private static DateTime? FileMtimeDate(string path)
    {
        try { return File.GetLastWriteTime(path).Date; }
        catch { return null; }
    }

    // ============================ Temps ============================
    public static int? ToSeconds(string? hhmmss)
    {
        if (string.IsNullOrEmpty(hhmmss)) return null;
        var p = hhmmss.Split(':');
        if (p.Length != 3) return null;
        if (int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return h * 3600 + m * 60 + s;
        return null;
    }

    public static string? SecondsToHhmmss(int? sec)
    {
        if (sec is null) return null;
        int v = sec.Value;
        return $"{v / 3600:00}:{v % 3600 / 60:00}:{v % 60:00}";
    }

    private static int? TryExtractTime(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = TimeRe.Match(text);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
    }

    private static int? TimeFromIso(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        var m = IsoTimeRe.Match(iso);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
    }

    // ============================ Texte ============================
    private static string CollapseWs(string s) => WsRe.Replace(s, " ").Trim();

    private static string NormalizeMsg(string? msg)
    {
        if (msg == null) return "";
        msg = msg.Trim();
        if (msg.Equals("NA", StringComparison.OrdinalIgnoreCase)) return "";
        return CollapseWs(msg).ToLowerInvariant();
    }

    private static bool LooksLikeError(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.ToLowerInvariant();
        return ErrorKeywords.Any(k => t.Contains(k));
    }

    private static double[]? ParseNumList(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        line = line.Trim();
        if (!NumListRe.IsMatch(line)) return null;
        var vals = new List<double>();
        foreach (var tok in line.Split(';'))
        {
            var t = tok.Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            vals.Add(v);
        }
        return vals.ToArray();
    }

    private static List<string> SplitCompoundErrors(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>();
        foreach (var raw in text.Split(';'))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            var key = CollapseWs(p).ToLowerInvariant();
            if (ExcludeSet.Contains(key)) continue;
            if (p.All(char.IsDigit)) continue;
            if (seen.Add(key)) result.Add(p);
        }
        return result;
    }

    public static string CleanMessageText(string? s)
    {
        if (s == null) return "";
        s = WebUtility.HtmlDecode(s).Trim();
        while (true)
        {
            var m = TagWrapRe.Match(s);
            if (!m.Success) break;
            var inner = m.Groups[3].Value.Trim();
            if (inner.Length >= s.Length) break; // securite anti-boucle
            s = inner;
        }
        s = AnyTagRe.Replace(s, "");
        return CollapseWs(s);
    }

    private static string CanonicalLabel(string msg)
    {
        var s = CollapseWs(CleanMessageText(msg)).ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    // ============================ Offsets ============================
    private static Dictionary<string, double> ParseOffsetsFromLine(string? line)
    {
        var res = new Dictionary<string, double>();
        if (string.IsNullOrEmpty(line)) return res;
        foreach (Match m in OffsetLineRe.Matches(line))
        {
            var key = OffsetKeys.FirstOrDefault(k => k.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                res[key] = v;
        }
        return res;
    }

    // ============================ Helpers XML ============================
    private static string? Attr(XElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var a = e.Attribute(n);
            if (a != null && a.Value.Length > 0) return a.Value;
        }
        return null;
    }

    /// <summary>Equivalent de elem.text (ElementTree) : texte avant le premier sous-element.</summary>
    private static string ElemText(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var n in e.Nodes())
        {
            if (n is XElement) break;
            if (n is XText t) sb.Append(t.Value);
        }
        return sb.ToString();
    }

    private static int? TimeOf(XElement el, string text)
    {
        var t = Attr(el, "Time");
        return !string.IsNullOrEmpty(t) ? ToSeconds(t) : TryExtractTime(text);
    }

    // ============================ Parsers ============================
    /// <summary>Retourne null si le fichier n'est pas un XML valide (on bascule alors en lecture texte).</summary>
    private static (List<LogEvent> events, List<Calibration> cals)? ParseXmlFile(string path, DateTime? fallback)
    {
        XElement root;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            var doc = XDocument.Load(reader);
            if (doc.Root == null) return null;
            root = doc.Root;
        }
        catch { return null; }

        var events = new List<LogEvent>();
        var cals = new List<Calibration>();
        var all = root.DescendantsAndSelf().ToList();

        // Date du fichier : premier element portant un attribut Date
        DateTime? dateStr = null;
        foreach (var el in all)
        {
            var a = el.Attribute("Date");
            if (a != null) { dateStr = NormalizeDateAttr(a.Value, fallback); break; }
        }
        dateStr ??= fallback;

        foreach (var el in all)
        {
            var tag = el.Name.LocalName;
            var text = ElemText(el);

            // --- Lentille marquee ---
            if (el.Attribute("Status")?.Value == "Marked")
            {
                var mt = el.Attribute("MarkingTime")?.Value;
                double? mtVal = null;
                if (mt != null && PlainNumRe.IsMatch(mt)
                    && double.TryParse(mt, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv))
                    mtVal = mv;
                events.Add(new LogEvent { Date = dateStr, TimeS = TimeOf(el, text), Marked = true, MarkingTime = mtVal });
            }

            // --- HealthLog ---
            if (tag == "HealthLog")
            {
                var goRaw = Attr(el, "Go", "GO", "go");
                bool? goVal = null;
                if (goRaw != null)
                {
                    var g = goRaw.Trim().ToLowerInvariant();
                    if (g == "true") goVal = true;
                    else if (g == "false") goVal = false;
                }
                var reasons = new List<string>();
                bool anyChildFalse = false;
                foreach (var child in el.Elements())
                {
                    var directName = child.Name.LocalName.Trim();
                    foreach (var node in child.DescendantsAndSelf())
                    {
                        var goNodeRaw = Attr(node, "Go", "GO", "go");
                        if (goNodeRaw == null) continue;
                        if (goNodeRaw.Trim().ToLowerInvariant() != "true")
                        {
                            anyChildFalse = true;
                            var statusName = Attr(node, "Status", "STATUS", "status") ?? node.Name.LocalName;
                            reasons.Add($"{directName} — {CollapseWs(statusName)}");
                        }
                    }
                }
                goVal ??= !anyChildFalse;

                var ev = new LogEvent { Date = dateStr, TimeS = TimeOf(el, text), IsHealth = true, HealthGo = goVal };
                if (goVal == false)
                {
                    var seen = new HashSet<string>();
                    foreach (var r in reasons)
                        if (seen.Add(CollapseWs(r).ToLowerInvariant())) ev.HealthReasons.Add(r);
                }
                events.Add(ev);
            }

            // --- Calibration ---
            if (tag == CalibTag)
            {
                int? t;
                if (el.Attribute("Date") != null) t = TimeFromIso(el.Attribute("Date")!.Value);
                else if (!string.IsNullOrEmpty(Attr(el, "Time"))) t = ToSeconds(Attr(el, "Time"));
                else t = TryExtractTime(text);

                double[]? xs = null, ys = null;
                var numericLines = text.Split('\n').Select(l => l.Trim()).Where(l => ParseNumList(l) != null).ToList();
                if (numericLines.Count >= 2) { xs = ParseNumList(numericLines[0]); ys = ParseNumList(numericLines[1]); }

                if ((xs == null || ys == null) && el.HasElements)
                {
                    var childNum = new List<string>();
                    foreach (var ch in el.Elements())
                    {
                        var ct = ElemText(ch).Trim();
                        if (ct.Length > 0 && ParseNumList(ct) != null) childNum.Add(ct);
                    }
                    if (childNum.Count >= 2) { xs = ParseNumList(childNum[0]); ys = ParseNumList(childNum[1]); }
                }

                if (xs == null || ys == null)
                {
                    var attrLists = el.Attributes().Select(a => a.Value.Trim()).Where(v => ParseNumList(v) != null).ToList();
                    if (attrLists.Count >= 2) { xs = ParseNumList(attrLists[0]); ys = ParseNumList(attrLists[1]); }
                }

                if (xs != null && ys != null && xs.Length > 1 && xs.Length == ys.Length)
                    cals.Add(new Calibration { Date = dateStr, TimeS = t, Xs = xs, Ys = ys });
            }

            // --- Offsets dans le texte ---
            if (text.Length > 0)
            {
                var offs = ParseOffsetsFromLine(text);
                if (offs.Count > 0)
                    events.Add(new LogEvent { Date = dateStr, TimeS = TimeOf(el, text), Offsets = offs });
            }
        }

        // --- Erreurs via <Event> ---
        foreach (var ev in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Event"))
        {
            var evDate = NormalizeDateAttr(ev.Attribute("Date")?.Value, dateStr) ?? dateStr;
            int? tEv = null;
            var tAttr = Attr(ev, "Time");
            if (tAttr != null) tEv = ToSeconds(tAttr);
            if (tEv == null && Attr(ev, "Date") != null) tEv = TimeFromIso(Attr(ev, "Date"));

            var msgs = new List<string>();
            foreach (var ch in ev.Elements())
            {
                var ctag = ch.Name.LocalName.Trim().ToLowerInvariant();
                var ctext = ElemText(ch);
                if ((ctag == "message" || ctag == "msg") && ctext.Trim().Length > 0)
                {
                    var txt = CleanMessageText(ctext);
                    if (LooksLikeError(txt))
                        foreach (var frag in SplitCompoundErrors(txt)) msgs.Add(CleanMessageText(frag));
                }
            }
            var evText = ElemText(ev);
            if (msgs.Count == 0 && evText.Length > 0)
            {
                var maybe = CleanMessageText(evText);
                if (LooksLikeError(maybe))
                    foreach (var frag in SplitCompoundErrors(maybe)) msgs.Add(CleanMessageText(frag));
            }
            foreach (var m in msgs)
                events.Add(new LogEvent { Date = evDate, TimeS = tEv, Msg = m });
        }

        return (events, cals);
    }

    private static (List<LogEvent> events, List<Calibration> cals) ParseTextFile(string path, DateTime? fallback)
    {
        var events = new List<LogEvent>();
        var cals = new List<Calibration>();
        var date = fallback;

        // etat de lecture d'une calibration (ligne X puis ligne Y)
        bool expecting = false;
        int? calTime = null;
        double[]? calXs = null;
        bool waitingForX = true;

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var t = TryExtractTime(line);
            var low = line.ToLowerInvariant();

            var mtM = MarkingTimeRe.Match(line);
            if (mtM.Success)
            {
                double? mt = double.TryParse(mtM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                events.Add(new LogEvent { Date = date, TimeS = t, Marked = true, MarkingTime = mt });
            }

            var offs = ParseOffsetsFromLine(line);
            if (offs.Count > 0)
                events.Add(new LogEvent { Date = date, TimeS = t, Offsets = offs });

            if (low.Contains("healthlog"))
            {
                var goM = GoRe.Match(line);
                var stM = StatusRe.Match(line);
                bool? goVal = goM.Success ? goM.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
                var ev = new LogEvent { Date = date, TimeS = t, IsHealth = true, HealthGo = goVal };
                if (goVal == false && stM.Success)
                {
                    var reason = stM.Groups[1].Value.Trim();
                    if (reason.Length > 0) ev.HealthReasons.Add(reason);
                }
                events.Add(ev);
                continue;
            }

            if (low.Contains(CalibTag.ToLowerInvariant()))
            {
                expecting = true; calTime = t; calXs = null; waitingForX = true;
                continue;
            }

            if (expecting)
            {
                if (NumListRe.IsMatch(line))
                {
                    var nums = ParseNumList(line);
                    if (waitingForX && nums != null) { calXs = nums; waitingForX = false; continue; }
                    if (!waitingForX && nums != null)
                    {
                        if (calXs != null && calXs.Length > 1 && calXs.Length == nums.Length)
                            cals.Add(new Calibration { Date = date, TimeS = calTime, Xs = calXs, Ys = nums });
                        expecting = false;
                        continue;
                    }
                }
                else expecting = false;
            }

            if (LooksLikeError(line))
            {
                foreach (var frag in SplitCompoundErrors(line))
                {
                    var clean = CleanMessageText(frag);
                    events.Add(new LogEvent { Date = date, TimeS = t ?? TryExtractTime(clean), Msg = clean });
                }
            }
        }
        return (events, cals);
    }

    // ============================ Agregation + dedup ============================
    private static void Inc<TK>(Dictionary<TK, int> d, TK key, int n = 1) where TK : notnull
        => d[key] = d.TryGetValue(key, out var v) ? v + n : n;

    private static void IncNested(Dictionary<DateTime, Dictionary<string, int>> d, DateTime date, string key)
    {
        if (!d.TryGetValue(date, out var inner)) d[date] = inner = new Dictionary<string, int>();
        Inc(inner, key);
    }

    /// <summary>La deduplication s'applique fichier par fichier, comme dans le script Python.</summary>
    private static void Aggregate(List<LogEvent> events, int dedupSec, SiteMetrics target)
    {
        var lastSeenTime = new Dictionary<(DateTime, string), int>();
        var lastSeenPrev = new Dictionary<DateTime, string?>();

        foreach (var ev in events)
        {
            if (ev.Date is not DateTime d) continue;

            if (ev.Marked)
            {
                Inc(target.Lenses, d);
                if (ev.MarkingTime is double mt)
                {
                    if (!target.MarkingTimes.TryGetValue(d, out var list)) target.MarkingTimes[d] = list = new List<double>();
                    list.Add(mt);
                }
                continue;
            }

            if (ev.IsHealth)
            {
                Inc(target.HealthTotal, d);
                if (ev.HealthGo == true) Inc(target.HealthOk, d);
                else if (ev.HealthGo == false)
                {
                    Inc(target.HealthKo, d);
                    foreach (var r in ev.HealthReasons)
                    {
                        var rr = CollapseWs(r);
                        if (rr.Length > 0) IncNested(target.HealthKoReasons, d, rr);
                    }
                }
                continue;
            }

            if (string.IsNullOrEmpty(ev.Msg)) continue;
            var key = NormalizeMsg(ev.Msg);
            if (key.Length == 0) continue;
            var label = CanonicalLabel(ev.Msg);

            if (ev.TimeS is int t)
            {
                if (!lastSeenTime.TryGetValue((d, key), out var last) || t - last >= dedupSec)
                {
                    IncNested(target.Errors, d, label);
                    lastSeenTime[(d, key)] = t;
                }
                lastSeenPrev[d] = key;
            }
            else
            {
                lastSeenPrev.TryGetValue(d, out var prev);
                if (prev != key)
                {
                    IncNested(target.Errors, d, label);
                    lastSeenPrev[d] = key;
                }
            }
        }
    }

    // ============================ Scan des dossiers ============================
    private static bool IsLogFile(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".xml") || n.EndsWith(".log") || n.EndsWith(".txt");
    }

    private static IEnumerable<FileInfo> EnumerateLogFiles(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return new DirectoryInfo(root).EnumerateFiles("*", opts).Where(f => IsLogFile(f.Name));
    }

    /// <summary>Signature rapide du contenu du dossier (equivalent du "manifest" du script Python).</summary>
    public static string ManifestSignature(string root)
    {
        long n = 0, mt = 0, sz = 0;
        int h = 0;
        unchecked
        {
            foreach (var f in EnumerateLogFiles(root))
            {
                try
                {
                    n++;
                    mt += f.LastWriteTimeUtc.Ticks;
                    sz += f.Length;
                    h ^= f.FullName.GetHashCode();
                }
                catch { }
            }
        }
        return $"{n}|{mt}|{sz}|{h}";
    }

    public static LogIndex BuildIndex(string logRoot, int dedupSec)
    {
        var idx = new LogIndex();

        // Dossiers "journaliers" : tout dossier qui contient au moins un fichier de log
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in EnumerateLogFiles(logRoot))
            if (f.DirectoryName != null) folders.Add(f.DirectoryName);

        foreach (var folder in folders)
        {
            var rel = Path.GetRelativePath(logRoot, folder);
            var site = (rel == "." || rel.Length == 0)
                ? "(Root)"
                : rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            if (!idx.Sites.TryGetValue(site, out var sm)) idx.Sites[site] = sm = new SiteMetrics();

            var dayName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var folderDate = NormalizeAnyDate(dayName);

            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch { continue; }
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var fpath in files)
            {
                var fname = Path.GetFileName(fpath);
                if (!IsLogFile(fname)) continue;
                var fileFallback = folderDate ?? FileMtimeDate(fpath);

                List<LogEvent> evts;
                List<Calibration> cals;
                try
                {
                    var xml = ParseXmlFile(fpath, fileFallback);
                    (evts, cals) = xml ?? ParseTextFile(fpath, fileFallback);
                }
                catch { continue; } // fichier illisible : on l'ignore
                idx.FilesParsed++;

                foreach (var e in evts) e.Date ??= fileFallback;
                foreach (var c in cals) c.Date ??= fileFallback;

                foreach (var e in evts)
                {
                    if (e.Offsets != null && e.Date is DateTime od)
                    {
                        if (!idx.OffsetObservations.TryGetValue(site, out var list))
                            idx.OffsetObservations[site] = list = new List<OffsetObs>();
                        list.Add(new OffsetObs { Site = site, Date = od, TimeS = e.TimeS, File = fname, Offsets = e.Offsets });
                    }
                }

                Aggregate(evts, dedupSec, sm);

                foreach (var c in cals)
                {
                    if (c.Date is not DateTime cd) continue;
                    Inc(sm.CalibrationsPerDate, cd);
                    if (!sm.CalibrationsData.TryGetValue(cd, out var cl)) sm.CalibrationsData[cd] = cl = new List<Calibration>();
                    cl.Add(c);
                }
            }
        }

        idx.OffsetChanges = BuildOffsetChangeLog(idx.OffsetObservations);
        return idx;
    }

    // ============================ Journal des offsets ============================
    public static List<OffsetObs> SortObservations(IEnumerable<OffsetObs> obs)
        => obs.OrderBy(o => o.Date).ThenBy(o => o.TimeS ?? 1_000_000_000).ToList();

    private static List<OffsetChange> BuildOffsetChangeLog(Dictionary<string, List<OffsetObs>> obsBySite)
    {
        var changes = new List<OffsetChange>();
        foreach (var (site, list) in obsBySite)
        {
            var last = new Dictionary<string, double>();
            foreach (var obs in SortObservations(list))
            {
                foreach (var p in OffsetKeys)
                {
                    if (!obs.Offsets.TryGetValue(p, out var v)) continue;
                    if (!last.TryGetValue(p, out var prev)) { last[p] = v; continue; }
                    if (v != prev)
                    {
                        changes.Add(new OffsetChange
                        {
                            Site = site, Date = obs.Date, TimeS = obs.TimeS,
                            Param = p, Old = prev, New = v, File = obs.File
                        });
                        last[p] = v;
                    }
                }
            }
        }
        return changes;
    }
}

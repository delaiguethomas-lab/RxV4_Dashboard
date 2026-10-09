// ================================================================
// UV Laser Logs Analysis
// Reecriture C#/.NET (WinForms) de l'application Streamlit
// "Logs_Analysis.py". Aucune dependance Python : compile en .exe
// natif Windows (voir .github/workflows/build.yml).
//
// Meme architecture que "Poste d'entree - Volumes" :
//  - Program.cs   : point d'entree + fenetre principale (UI)
//  - LogEngine.cs : parsing / agregation des logs (sans UI)
//  - config.txt   : reglages optionnels poses a cote de l'exe
//  - logo.png     : logo affiche en haut a droite
//
// Fonctions reprises du script d'origine :
//  - Mode "Single Machine" : sante machine (HealthLog), calibrations
//    d'energie + courbes, lentilles/jour, Pareto des defauts cliquable,
//    temps de marquage moyen/jour, journal + tendances des offsets,
//    export Excel par site
//  - Mode "Comparison" : synthese multi-sites, sante, temps de marquage,
//    lentilles/jour, Pareto multi-sites cliquable
//  - Un seul selecteur de periode pour toutes les sections
// ================================================================

using System.Data;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Label = System.Windows.Forms.Label; // evite l'ambiguite avec Charting.Label

namespace LogsAnalysis;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// ---------------------------------------------------------------
// Configuration (config.txt a cote de l'exe, tout est optionnel)
// ---------------------------------------------------------------
internal sealed class AppConfig
{
    public string LogRoot { get; private set; } = "";
    public int DedupSeconds { get; private set; } = LogEngine.DefaultDedupSeconds;
    public int RefreshSeconds { get; private set; } = 0;

    public static string FilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.txt");

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        try
        {
            if (!File.Exists(FilePath)) return cfg;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                int i = t.IndexOf('=');
                if (i <= 0) continue;
                var key = t[..i].Trim().ToUpperInvariant();
                var val = t[(i + 1)..].Trim();
                if (key == "LOG_ROOT") cfg.LogRoot = val;
                else if (key == "DEDUP_SECONDS" && int.TryParse(val, out var d) && d >= 0) cfg.DedupSeconds = d;
                else if (key == "REFRESH_SECONDS" && int.TryParse(val, out var r) && r >= 0) cfg.RefreshSeconds = r;
            }
        }
        catch { /* config illisible : valeurs par defaut */ }
        return cfg;
    }

    /// <summary>Met a jour (ou ajoute) une ligne CLE=valeur dans config.txt. Silencieux en cas d'echec.</summary>
    public static void SaveValue(string key, string value)
    {
        try
        {
            var lines = File.Exists(FilePath)
                ? File.ReadAllLines(FilePath).ToList()
                : new List<string> { "# Configuration Logs Analysis (voir config.example.txt)" };
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    found = true;
                    break;
                }
            }
            if (!found) lines.Add(key + "=" + value);
            File.WriteAllLines(FilePath, lines);
        }
        catch { /* dossier en lecture seule : on ignore */ }
    }
}

// ---------------------------------------------------------------
// Zone de contenu defilante : empile des blocs (graphiques, tableaux...)
// ---------------------------------------------------------------
internal sealed class ContentPanel : FlowLayoutPanel
{
    public ContentPanel()
    {
        Dock = DockStyle.Fill;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = Color.White;
        Padding = new Padding(10);
    }

    private int ChildWidth() =>
        Math.Max(320, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);

    public void Add(Control c)
    {
        c.Margin = new Padding(0, 0, 0, 12);
        c.Width = ChildWidth();
        Controls.Add(c);
    }

    public void Clear()
    {
        foreach (var c in Controls.Cast<Control>().ToList())
        {
            Controls.Remove(c);
            c.Dispose();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int w = ChildWidth();
        foreach (Control c in Controls) c.Width = w;
    }
}

// ---------------------------------------------------------------
// Fenetre principale
// ---------------------------------------------------------------
public class MainForm : Form
{
    private static readonly string LogoPath = Path.Combine(AppContext.BaseDirectory, "logo.png");
    private static readonly string[] Palette =
        { "#5DADE2", "#FF7F50", "#9ACD32", "#8A2BE2", "#F4D03F", "#E67E22", "#2ECC71", "#D62728" };

    // ---------- Etat ----------
    private readonly AppConfig _cfg = AppConfig.Load();
    private string _logRoot = "";
    private LogIndex? _index;
    private string _manifestSig = "";
    private bool _busy;
    private bool _uiLock; // bloque les evenements pendant le remplissage de l'UI

    private bool _hasPeriod;
    private DateTime _periodStart, _periodEnd, _minDate, _maxDate;

    private string? _selErrSingle;
    private string? _selErrCmp;

    // ---------- Controles : en-tete / barre d'etat ----------
    private readonly Label _lblCaption = new();
    private readonly Label _statusBar = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    // ---------- Controles : panneau lateral ----------
    private readonly TextBox _txtFolder = new();
    private readonly RadioButton _rbSingle = new() { Text = "Single Machine", Checked = true, AutoSize = true };
    private readonly RadioButton _rbCompare = new() { Text = "Comparison", AutoSize = true };
    private GroupBox _grpSingle = null!;
    private GroupBox _grpCompare = null!;
    private readonly ComboBox _cmbSite = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 262 };
    private readonly CheckedListBox _clbSites = new() { Width = 262, Height = 150, CheckOnClick = true };
    private readonly DateTimePicker _dtFrom = new() { Format = DateTimePickerFormat.Short, Width = 262 };
    private readonly DateTimePicker _dtTo = new() { Format = DateTimePickerFormat.Short, Width = 262 };

    // ---------- Controles : barres d'outils des onglets Pareto ----------
    private readonly TextBox _txtParetoS = new() { Width = 220 };
    private readonly NumericUpDown _numTopS = new() { Minimum = 5, Maximum = 50, Value = 20, Width = 60 };
    private readonly CheckBox _chkHoriz = new() { Text = "Horizontal view", AutoSize = true };
    private readonly TextBox _txtParetoC = new() { Width = 220 };
    private readonly NumericUpDown _numTopC = new() { Minimum = 5, Maximum = 30, Value = 15, Width = 60 };
    private readonly CheckBox _chkNorm = new() { Text = "Normalize by site (%)", AutoSize = true };

    // ---------- Onglets ----------
    private sealed class TabDef
    {
        public TabPage Page { get; }
        public ContentPanel Content { get; } = new();
        public FlowLayoutPanel? Toolbar { get; }
        public Action Render { get; set; } = () => { };

        public TabDef(string title, bool withToolbar)
        {
            Page = new TabPage(title) { BackColor = Color.White };
            Page.Controls.Add(Content); // Fill ajoute en premier
            if (withToolbar)
            {
                Toolbar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    BackColor = Color.FromArgb(247, 249, 252),
                    Padding = new Padding(8, 6, 8, 6)
                };
                Page.Controls.Add(Toolbar); // Top ajoute apres => range au-dessus
            }
        }
    }

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<TabDef> _singleTabs = new();
    private readonly List<TabDef> _compareTabs = new();
    private List<TabDef> _activeTabs = new();

    private TabDef _tHealth = null!, _tCal = null!, _tLens = null!, _tParetoS = null!,
                   _tMark = null!, _tOff = null!, _tExport = null!;
    private TabDef _cSummary = null!, _cHealth = null!, _cMark = null!, _cLens = null!, _cPareto = null!;

    // ============================================================
    // Construction
    // ============================================================
    public MainForm()
    {
        Text = "UV Laser Logs Analysis";
        Width = 1320;
        Height = 860;
        MinimumSize = new Size(1000, 650);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;

        BuildTabs();

        // Ordre d'ajout important en WinForms : le dernier ajoute est "dock" en premier.
        // => zone centrale (Fill) d'abord, puis panneau gauche, barre d'etat, en-tete.
        Controls.Add(_tabs);
        Controls.Add(BuildSidebar());
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildHeader());

        _rbSingle.CheckedChanged += (s, e) => { if (!_uiLock) SwitchMode(); };
        SwitchMode();

        _timer.Tick += async (s, e) => await ReloadAsync(false);
        if (_cfg.RefreshSeconds > 0)
        {
            _timer.Interval = _cfg.RefreshSeconds * 1000;
            _timer.Start();
        }

        Load += async (s, e) => await StartupAsync();
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Color.White };

        if (File.Exists(LogoPath))
        {
            try
            {
                var logo = new PictureBox
                {
                    Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(LogoPath))),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Dock = DockStyle.Right,
                    Width = 70
                };
                header.Controls.Add(logo);
            }
            catch { /* logo illisible : on continue sans */ }
        }

        header.Controls.Add(new Label
        {
            Text = "UV Laser Log Files Analysis",
            Font = new Font("Arial", 16, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(15, 8)
        });

        _lblCaption.AutoSize = true;
        _lblCaption.Font = new Font("Arial", 9.5f);
        _lblCaption.ForeColor = Color.DimGray;
        _lblCaption.Location = new Point(17, 42);
        header.Controls.Add(_lblCaption);
        return header;
    }

    private Control BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = 24;
        _statusBar.BorderStyle = BorderStyle.Fixed3D;
        _statusBar.TextAlign = ContentAlignment.MiddleLeft;
        _statusBar.Text = "";
        return _statusBar;
    }

    private static GroupBox Group(string title, params Control[] children)
    {
        var g = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 4, 6, 6),
            Margin = new Padding(0, 0, 0, 8)
        };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(6, 20)
        };
        flow.Controls.AddRange(children);
        g.Controls.Add(flow);
        return g;
    }

    private Control BuildSidebar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 306,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(247, 249, 252),
            Padding = new Padding(8)
        };

        // --- Dossier ---
        _txtFolder.ReadOnly = true;
        _txtFolder.Width = 262;
        var btnBrowse = new Button { Text = "Browse...", Width = 128, Height = 28 };
        var btnReload = new Button { Text = "Reload", Width = 128, Height = 28 };
        btnBrowse.Click += async (s, e) => await BrowseAsync();
        btnReload.Click += async (s, e) => await ReloadAsync(true);
        var rowBtns = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = 262
        };
        rowBtns.Controls.Add(btnBrowse);
        rowBtns.Controls.Add(btnReload);
        panel.Controls.Add(Group("Logs folder", _txtFolder, rowBtns));

        // --- Mode ---
        panel.Controls.Add(Group("Analysis mode", _rbSingle, _rbCompare));

        // --- Machine / sites ---
        _grpSingle = Group("Machine filter", _cmbSite);
        _grpCompare = Group("Sites to compare (2 to 6)", _clbSites);
        panel.Controls.Add(_grpSingle);
        panel.Controls.Add(_grpCompare);
        _cmbSite.SelectedIndexChanged += (s, e) => { if (!_uiLock) RenderAll(); };
        _clbSites.ItemCheck += (s, e) =>
        {
            if (!_uiLock) BeginInvoke(new Action(() => { if (_rbCompare.Checked) RenderAll(); }));
        };

        // --- Periode ---
        var btnFull = new Button { Text = "Full range", Width = 262, Height = 26 };
        btnFull.Click += (s, e) => ResetPeriod();
        panel.Controls.Add(Group("Period",
            new Label { Text = "From", AutoSize = true }, _dtFrom,
            new Label { Text = "To", AutoSize = true }, _dtTo,
            btnFull));
        _dtFrom.ValueChanged += OnPeriodChanged;
        _dtTo.ValueChanged += OnPeriodChanged;

        return panel;
    }

    private static Label ToolLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(6, 7, 3, 0) };

    private void BuildTabs()
    {
        // ----- Mode Single Machine -----
        _tHealth = new TabDef("Machine Health", false) { Render = RenderHealthSingle };
        _tCal = new TabDef("Calibrations", false) { Render = RenderCalibrations };
        _tLens = new TabDef("Lenses / day", false) { Render = RenderLensesSingle };
        _tParetoS = new TabDef("Defects Pareto", true) { Render = RenderParetoSingle };
        _tMark = new TabDef("Marking time", false) { Render = RenderMarkingSingle };
        _tOff = new TabDef("Offsets", false) { Render = RenderOffsets };
        _tExport = new TabDef("Export", false) { Render = RenderExport };
        _singleTabs.AddRange(new[] { _tHealth, _tCal, _tLens, _tParetoS, _tMark, _tOff, _tExport });

        _tParetoS.Toolbar!.Controls.AddRange(new Control[]
        {
            ToolLabel("Defect name filter:"), _txtParetoS,
            ToolLabel("Top N"), _numTopS,
            _chkHoriz
        });
        _chkHoriz.Margin = new Padding(12, 6, 3, 0);
        _txtParetoS.TextChanged += (s, e) => { if (!_uiLock) SafeRender(_tParetoS); };
        _numTopS.ValueChanged += (s, e) => { if (!_uiLock) SafeRender(_tParetoS); };
        _chkHoriz.CheckedChanged += (s, e) => { if (!_uiLock) SafeRender(_tParetoS); };

        // ----- Mode Comparison -----
        _cSummary = new TabDef("Summary", false) { Render = RenderCmpSummary };
        _cHealth = new TabDef("Machine Check", false) { Render = RenderCmpHealth };
        _cMark = new TabDef("Marking time", false) { Render = RenderCmpMarking };
        _cLens = new TabDef("Lenses / day", false) { Render = RenderCmpLenses };
        _cPareto = new TabDef("Defects Pareto", true) { Render = RenderCmpPareto };
        _compareTabs.AddRange(new[] { _cSummary, _cHealth, _cMark, _cLens, _cPareto });

        _cPareto.Toolbar!.Controls.AddRange(new Control[]
        {
            ToolLabel("Text filter (contains):"), _txtParetoC,
            ToolLabel("Top N"), _numTopC,
            _chkNorm
        });
        _chkNorm.Margin = new Padding(12, 6, 3, 0);
        _txtParetoC.TextChanged += (s, e) => { if (!_uiLock) SafeRender(_cPareto); };
        _numTopC.ValueChanged += (s, e) => { if (!_uiLock) SafeRender(_cPareto); };
        _chkNorm.CheckedChanged += (s, e) => { if (!_uiLock) SafeRender(_cPareto); };
    }

    // ============================================================
    // Demarrage, chargement des logs
    // ============================================================
    private string? PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the logs root folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (!string.IsNullOrEmpty(_logRoot) && Directory.Exists(_logRoot)) dlg.SelectedPath = _logRoot;
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    private async Task StartupAsync()
    {
        var root = _cfg.LogRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            var def = Path.Combine(AppContext.BaseDirectory, "Log");
            root = Directory.Exists(def) ? def : (PickFolder() ?? "");
        }
        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("No logs folder selected. Click \"Browse...\" to choose one.");
            return;
        }
        _logRoot = root;
        _txtFolder.Text = root;
        await ReloadAsync(true);
    }

    private async Task BrowseAsync()
    {
        if (_busy) return;
        var p = PickFolder();
        if (p == null) return;
        _logRoot = p;
        _txtFolder.Text = p;
        AppConfig.SaveValue("LOG_ROOT", p);
        await ReloadAsync(true);
    }

    private async Task ReloadAsync(bool force)
    {
        if (_busy || string.IsNullOrEmpty(_logRoot)) return;
        if (!Directory.Exists(_logRoot))
        {
            SetStatus($"Folder '{_logRoot}' not found.");
            return;
        }

        _busy = true;
        try
        {
            var root = _logRoot;
            var dedup = _cfg.DedupSeconds;
            var sig = await Task.Run(() => LogEngine.ManifestSignature(root));
            if (!force && _index != null && sig == _manifestSig) return; // rien n'a change

            SetStatus("Indexing logs...");
            UseWaitCursor = true;
            var idx = await Task.Run(() => LogEngine.BuildIndex(root, dedup));
            _index = idx;
            _manifestSig = sig;
            ApplyIndexToUi();
            SetStatus($"{root}  |  {idx.FilesParsed} files, {idx.Sites.Count} site(s)  |  Last update: {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SetStatus("Error while indexing logs: " + ex.Message);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
        }
    }

    private void SetStatus(string text) => _statusBar.Text = text;

    private void ApplyIndexToUi()
    {
        if (_index == null) return;
        _uiLock = true;
        try
        {
            var sites = _index.SiteNames();

            var prevSite = _cmbSite.SelectedItem as string;
            var prevChecked = _clbSites.CheckedItems.Cast<object>().Select(o => o.ToString() ?? "").ToHashSet();

            _cmbSite.Items.Clear();
            foreach (var s in sites) _cmbSite.Items.Add(s);
            if (sites.Count > 0)
                _cmbSite.SelectedItem = (prevSite != null && sites.Contains(prevSite)) ? prevSite : sites[0];

            _clbSites.Items.Clear();
            for (int i = 0; i < sites.Count; i++)
            {
                bool chk = prevChecked.Count > 0 ? prevChecked.Contains(sites[i]) : i < 2;
                _clbSites.Items.Add(sites[i], chk);
            }

            // --- Periode : bornes = toutes les dates presentes dans les logs ---
            var dates = _index.AllDates().ToList();
            if (dates.Count > 0)
            {
                bool wasFull = !_hasPeriod || (_periodStart == _minDate && _periodEnd == _maxDate);
                _minDate = dates.Min().Date;
                _maxDate = dates.Max().Date;
                var from = wasFull ? _minDate : ClampDate(_periodStart, _minDate, _maxDate);
                var to = wasFull ? _maxDate : ClampDate(_periodEnd, _minDate, _maxDate);
                ConfigurePicker(_dtFrom, _minDate, _maxDate, from);
                ConfigurePicker(_dtTo, _minDate, _maxDate, to);
                _periodStart = from;
                _periodEnd = to;
                _hasPeriod = true;
                _dtFrom.Enabled = _dtTo.Enabled = true;
            }
            else
            {
                _hasPeriod = false;
                _dtFrom.Enabled = _dtTo.Enabled = false;
            }
        }
        finally { _uiLock = false; }

        RenderAll();
    }

    private static DateTime ClampDate(DateTime d, DateTime min, DateTime max) => d < min ? min : d > max ? max : d;

    private static void ConfigurePicker(DateTimePicker dtp, DateTime min, DateTime max, DateTime value)
    {
        dtp.MinDate = DateTimePicker.MinimumDateTime;
        dtp.MaxDate = DateTimePicker.MaximumDateTime;
        dtp.Value = value;
        dtp.MinDate = min;
        dtp.MaxDate = max;
    }

    private void ResetPeriod()
    {
        if (!_hasPeriod) return;
        _uiLock = true;
        try
        {
            _dtFrom.Value = _minDate;
            _dtTo.Value = _maxDate;
        }
        finally { _uiLock = false; }
        _periodStart = _minDate;
        _periodEnd = _maxDate;
        RenderAll();
    }

    private void OnPeriodChanged(object? sender, EventArgs e)
    {
        if (_uiLock || !_hasPeriod) return;
        var a = _dtFrom.Value.Date;
        var b = _dtTo.Value.Date;
        if (a > b) (a, b) = (b, a);
        _periodStart = a;
        _periodEnd = b;
        RenderAll();
    }

    // ============================================================
    // Mode / rendu global
    // ============================================================
    private void SwitchMode()
    {
        bool single = _rbSingle.Checked;
        _grpSingle.Visible = single;
        _grpCompare.Visible = !single;
        _activeTabs = single ? _singleTabs : _compareTabs;

        _tabs.SuspendLayout();
        _tabs.TabPages.Clear();
        foreach (var t in _activeTabs) _tabs.TabPages.Add(t.Page);
        _tabs.ResumeLayout();

        RenderAll();
    }

    private void RenderAll()
    {
        if (_index == null || _uiLock) return;
        UpdateCaption();
        foreach (var t in _activeTabs) SafeRender(t);
    }

    private void SafeRender(TabDef t)
    {
        if (_index == null) return;
        t.Content.SuspendLayout();
        try
        {
            t.Content.Clear();
            if (_index.Sites.Count == 0)
                t.Content.Add(Note("No site detected under the logs folder.", true));
            else
                t.Render();
        }
        catch (Exception ex)
        {
            t.Content.Clear();
            t.Content.Add(Note("Rendering error: " + ex.Message, true));
        }
        finally { t.Content.ResumeLayout(true); }
    }

    private void UpdateCaption()
    {
        var parts = new List<string>();
        if (_hasPeriod) parts.Add($"Active period: {PeriodText()}");
        if (_rbSingle.Checked)
        {
            if (_cmbSite.SelectedItem is string s) parts.Add($"Selected site: {s}");
        }
        else
        {
            var sel = CheckedSites();
            if (sel.Count > 0) parts.Add("Comparing: " + string.Join(", ", sel));
        }
        _lblCaption.Text = string.Join("   |   ", parts);
    }

    private string PeriodText() => _hasPeriod ? $"{Lbl(_periodStart)} -> {Lbl(_periodEnd)}" : "-";

    // ============================================================
    // Filtres et utilitaires de donnees
    // ============================================================
    private SiteMetrics? CurrentSite() =>
        _index != null && _cmbSite.SelectedItem is string name ? _index.Sites.GetValueOrDefault(name) : null;

    private List<string> CheckedSites() =>
        _clbSites.CheckedItems.Cast<object>().Select(o => o.ToString() ?? "").OrderBy(x => x, StringComparer.Ordinal).ToList();

    private bool InRange(DateTime d) => !_hasPeriod || (d.Date >= _periodStart && d.Date <= _periodEnd);

    private Dictionary<DateTime, T> FilterRange<T>(Dictionary<DateTime, T> src) =>
        src.Where(kv => InRange(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static List<DateTime> Sorted(IEnumerable<DateTime> dates) => dates.Distinct().OrderBy(d => d).ToList();

    private static string Lbl(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static Dictionary<string, int> SumCounters(IEnumerable<Dictionary<string, int>> counters)
    {
        var res = new Dictionary<string, int>();
        foreach (var c in counters)
            foreach (var kv in c)
                res[kv.Key] = res.GetValueOrDefault(kv.Key) + kv.Value;
        return res;
    }

    private static double MsToSeconds(double ms) => Math.Round(ms / 1000.0, 2);

    private List<DateTime> SiteDatesInPeriod(SiteMetrics sb)
    {
        var all = new HashSet<DateTime>(sb.Lenses.Keys);
        all.UnionWith(sb.HealthTotal.Keys);
        all.UnionWith(sb.HealthOk.Keys);
        all.UnionWith(sb.HealthKo.Keys);
        all.UnionWith(sb.MarkingTimes.Keys);
        all.UnionWith(sb.Errors.Keys);
        return all.Where(InRange).OrderBy(d => d).ToList();
    }

    // ============================================================
    // Fabrique de composants visuels
    // ============================================================
    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    private static Control Heading(string text) => new Label
    {
        Text = text,
        Font = new Font("Segoe UI", 13f, FontStyle.Bold),
        AutoSize = false,
        Height = 32,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Control Note(string text, bool warning = false) => new Label
    {
        Text = text,
        AutoSize = false,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = warning ? Color.Firebrick : Color.FromArgb(40, 90, 150)
    };

    private static Control MetricRow(params (string title, string value)[] items)
    {
        var row = new FlowLayoutPanel
        {
            Height = 72,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.White
        };
        foreach (var (title, value) in items)
        {
            var card = new Panel
            {
                Width = 220,
                Height = 62,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(247, 249, 252),
                Margin = new Padding(0, 0, 12, 0)
            };
            card.Controls.Add(new Label
            {
                Text = title,
                AutoSize = false,
                Location = new Point(8, 4),
                Size = new Size(200, 18),
                ForeColor = Color.DimGray
            });
            card.Controls.Add(new Label
            {
                Text = value,
                AutoSize = false,
                Location = new Point(8, 24),
                Size = new Size(200, 32),
                Font = new Font("Segoe UI", 16f, FontStyle.Bold)
            });
            row.Controls.Add(card);
        }
        return row;
    }

    private static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.White
        };
        foreach (var c in controls) { c.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(c); }
        return row;
    }

    private static Button MakeButton(string text, int width = 200) => new() { Text = text, Width = width, Height = 28 };

    private static DataGridView MakeGrid(DataTable dt, int maxHeight = 320)
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            DataSource = dt
        };
        grid.Height = Math.Min(maxHeight, 34 + 24 * dt.Rows.Count);
        return grid;
    }

    // ---------- Graphiques ----------
    private static Chart NewChart(string? title, string xTitle, string yTitle, int height,
                                  bool legend = false, int xAngle = 0, bool allLabels = false)
    {
        var chart = new Chart
        {
            Height = height,
            BackColor = Color.White,
            AntiAliasing = AntiAliasingStyles.All
        };
        var area = new ChartArea("area") { BackColor = Color.White };
        area.AxisX.Title = xTitle;
        area.AxisY.Title = yTitle;
        area.AxisX.MajorGrid.Enabled = false;
        area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
        area.AxisX.LabelStyle.Angle = xAngle;
        area.AxisX.LabelStyle.Font = new Font("Segoe UI", 8.5f);
        if (allLabels) area.AxisX.Interval = 1;
        chart.ChartAreas.Add(area);

        if (!string.IsNullOrEmpty(title))
            chart.Titles.Add(new Title(title, Docking.Top, new Font("Segoe UI", 11f, FontStyle.Bold), Color.Black));
        if (legend)
            chart.Legends.Add(new Legend("legend") { Docking = Docking.Top });
        return chart;
    }

    private static Series NewSeries(string name, SeriesChartType type, Color color) =>
        new(name) { ChartType = type, Color = color, BorderWidth = 2 };

    /// <summary>Ajoute un point categoriel (etiquette X texte) avec texte optionnel au-dessus de la barre.</summary>
    private static void AddPoint(Series s, string label, double y, string? text = null)
    {
        int i = s.Points.AddXY(label, y);
        var p = s.Points[i];
        p.AxisLabel = label;
        if (text != null) p.Label = text;
    }

    private void AttachClick(Chart chart, Action<string> onSelect)
    {
        chart.Cursor = Cursors.Hand;
        chart.MouseClick += (s, e) =>
        {
            HitTestResult hit = chart.HitTest(e.X, e.Y);
            if (hit.ChartElementType == ChartElementType.DataPoint && hit.Series != null && hit.PointIndex >= 0)
            {
                var label = hit.Series.Points[hit.PointIndex].AxisLabel;
                if (!string.IsNullOrEmpty(label))
                    BeginInvoke(new Action(() => onSelect(label))); // differe : le graphique va etre recree
            }
        };
    }

    // ---------- Export CSV ----------
    private static string SafeFile(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string CsvEscape(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private void SaveCsv(string defaultName, string[] header, IEnumerable<string[]> rows)
    {
        using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = SafeFile(defaultName) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", header.Select(CsvEscape)));
            foreach (var r in rows) sb.AppendLine(string.Join(";", r.Select(CsvEscape)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            SetStatus("Saved: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file:\n" + ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ============================================================
    // MODE SINGLE MACHINE
    // ============================================================

    // ---------- Machine Health ----------
    private void RenderHealthSingle()
    {
        var host = _tHealth.Content;
        var sb = CurrentSite();
        if (sb == null) { host.Add(Note("No site selected.")); return; }

        var fTot = FilterRange(sb.HealthTotal);
        var fOk = FilterRange(sb.HealthOk);
        var fKo = FilterRange(sb.HealthKo);
        var fReasons = FilterRange(sb.HealthKoReasons);

        if (fTot.Count == 0 && fOk.Count == 0 && fKo.Count == 0)
        {
            host.Add(Note("No machine check data in the selected period."));
            return;
        }

        host.Add(Heading("Machine Check Health - per day"));
        var dates = Sorted(fTot.Keys.Concat(fOk.Keys).Concat(fKo.Keys));
        var chart = NewChart(null, "Date", "Checks count", 380, legend: true);
        var sOk = NewSeries("OK (Go=True)", SeriesChartType.StackedColumn, C("#2E8B57"));
        var sKo = NewSeries("KO (Go=False)", SeriesChartType.StackedColumn, C("#DC143C"));
        sOk.ToolTip = sKo.ToolTip = "#SERIESNAME\n#AXISLABEL: #VAL";
        foreach (var d in dates)
        {
            AddPoint(sOk, Lbl(d), fOk.GetValueOrDefault(d));
            AddPoint(sKo, Lbl(d), fKo.GetValueOrDefault(d));
        }
        chart.Series.Add(sOk);
        chart.Series.Add(sKo);
        host.Add(chart);

        host.Add(MetricRow(
            ("Total", fTot.Values.Sum().ToString()),
            ("Passed (Go=True)", fOk.Values.Sum().ToString()),
            ("Failed (Go=False)", fKo.Values.Sum().ToString())));

        var agg = SumCounters(fReasons.Values);
        if (agg.Count > 0)
        {
            var chR = NewChart($"Check health (KO) reasons - {PeriodText()}", "Reason", "Occurrences", 420,
                               xAngle: -45, allLabels: true);
            var sR = NewSeries("Reasons", SeriesChartType.Column, C("#8A2BE2"));
            sR.ToolTip = "#AXISLABEL\n#VAL";
            foreach (var kv in agg.OrderByDescending(p => p.Value)) AddPoint(sR, kv.Key, kv.Value, kv.Value.ToString());
            chR.Series.Add(sR);
            host.Add(chR);
        }
        else host.Add(Note("No KO reason recorded in the selected period."));
    }

    // ---------- Calibrations ----------
    private void RenderCalibrations()
    {
        var host = _tCal.Content;
        var sb = CurrentSite();
        if (sb == null) { host.Add(Note("No site selected.")); return; }

        var perDay = FilterRange(sb.CalibrationsPerDate);
        var data = FilterRange(sb.CalibrationsData);
        if (perDay.Count == 0 && data.Count == 0)
        {
            host.Add(Note("No energy calibration in the selected period."));
            return;
        }

        if (perDay.Count > 0)
        {
            host.Add(Heading("Energy calibrations per day"));
            var chart = NewChart(null, "Date", "Calibrations count", 340);
            var ser = NewSeries("Calibrations", SeriesChartType.Column, C("#9ACD32"));
            ser.ToolTip = "#AXISLABEL\n#VAL";
            foreach (var d in Sorted(perDay.Keys)) AddPoint(ser, Lbl(d), perDay[d], perDay[d].ToString());
            chart.Series.Add(ser);
            host.Add(chart);
        }

        if (data.Count > 0)
        {
            host.Add(Heading("Energy calibration curves"));
            var flat = new List<(DateTime date, Calibration cal, string label)>();
            foreach (var d in Sorted(data.Keys))
                foreach (var cal in data[d])
                {
                    var tl = LogEngine.SecondsToHhmmss(cal.TimeS) ?? "No time";
                    flat.Add((d, cal, $"{Lbl(d)} - {tl} (# {cal.Xs.Length} pts)"));
                }

            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
            foreach (var f in flat) combo.Items.Add(f.label);

            var curve = NewChart(null, "Position", "Value", 380);
            curve.ChartAreas[0].AxisY.IsStartedFromZero = false;
            var btnExport = MakeButton("Export curve (CSV)", 180);

            void ShowCurve(int i)
            {
                if (i < 0 || i >= flat.Count) return;
                var item = flat[i];
                var tl = LogEngine.SecondsToHhmmss(item.cal.TimeS) ?? "No time";
                curve.Series.Clear();
                curve.Titles.Clear();
                curve.Titles.Add(new Title($"Curve - {Lbl(item.date)} - {tl}", Docking.Top,
                                           new Font("Segoe UI", 11f, FontStyle.Bold), Color.Black));
                var line = NewSeries("Calibration", SeriesChartType.Line, C("#FF7F50"));
                line.MarkerStyle = MarkerStyle.Circle;
                line.MarkerSize = 6;
                line.ToolTip = "#VALX ; #VAL{N3}";
                for (int k = 0; k < item.cal.Xs.Length; k++) line.Points.AddXY(item.cal.Xs[k], item.cal.Ys[k]);
                curve.Series.Add(line);
            }

            combo.SelectedIndexChanged += (s, e) => ShowCurve(combo.SelectedIndex);
            btnExport.Click += (s, e) =>
            {
                int i = combo.SelectedIndex;
                if (i < 0) return;
                var item = flat[i];
                var tl = (LogEngine.SecondsToHhmmss(item.cal.TimeS) ?? "notime").Replace(':', '-');
                var rows = item.cal.Xs.Select((x, k) => new[] { Num(x), Num(item.cal.Ys[k]) });
                SaveCsv($"calibration_{Lbl(item.date)}_{tl}.csv", new[] { "Position", "Value" }, rows);
            };

            host.Add(combo);
            host.Add(curve);
            host.Add(Row(btnExport));
            combo.SelectedIndex = 0;
        }
    }

    // ---------- Lentilles / jour ----------
    private void RenderLensesSingle()
    {
        var host = _tLens.Content;
        var sb = CurrentSite();
        if (sb == null) { host.Add(Note("No site selected.")); return; }

        var f = FilterRange(sb.Lenses);
        if (f.Count == 0) { host.Add(Note("No lenses data.", true)); return; }

        host.Add(Heading("Lenses engraved per day"));
        var chart = NewChart(null, "Date", "Lenses per day", 360);
        var ser = NewSeries("Lenses", SeriesChartType.Column, C("#87CEFA"));
        ser.ToolTip = "#AXISLABEL\n#VAL";
        foreach (var d in Sorted(f.Keys)) AddPoint(ser, Lbl(d), f[d], f[d].ToString());
        chart.Series.Add(ser);
        host.Add(chart);
    }

    // ---------- Pareto des defauts ----------
    private void RenderParetoSingle()
    {
        var host = _tParetoS.Content;
        var sb = CurrentSite();
        if (sb == null) { host.Add(Note("No site selected.")); return; }

        host.Add(Heading("Defects Pareto for the period"));
        var fErr = FilterRange(sb.Errors);
        var agg = SumCounters(fErr.Values);
        var q = _txtParetoS.Text.Trim().ToLowerInvariant();
        var filtered = q.Length == 0
            ? agg
            : agg.Where(kv => kv.Key.ToLowerInvariant().Contains(q)).ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0)
        {
            host.Add(Note("No defect found (or filtered out)."));
            return;
        }

        int top = (int)_numTopS.Value;
        var items = filtered.OrderByDescending(kv => kv.Value).Take(top).ToList();

        var chart = NewChart($"Defects Pareto - {PeriodText()}", "Defect", "Occurrences (deduplicated)", 520,
                             xAngle: -45, allLabels: true);
        var ser = NewSeries("Count", SeriesChartType.Column, C("#FA8072"));
        ser.ToolTip = "#AXISLABEL\n#VAL\nClick here for more details";
        foreach (var kv in items) AddPoint(ser, kv.Key, kv.Value, kv.Value.ToString());
        chart.Series.Add(ser);
        AttachClick(chart, name => { _selErrSingle = name; SafeRender(_tParetoS); });
        host.Add(chart);

        // --- Detail du defaut selectionne ---
        var sel = _selErrSingle;
        if (!string.IsNullOrEmpty(sel))
        {
            host.Add(Heading($"Selected defect details: {sel}"));
            var dates = Sorted(fErr.Keys);
            var yv = dates.Select(d => fErr[d].GetValueOrDefault(sel)).ToList();
            var chD = NewChart($"Occurrences per day - {PeriodText()}", "Date", "Occurrences (deduplicated)", 340);
            var sD = NewSeries("Occurrences", SeriesChartType.Column, C("#1F77B4"));
            sD.ToolTip = "#AXISLABEL\n#VAL";
            for (int i = 0; i < dates.Count; i++) AddPoint(sD, Lbl(dates[i]), yv[i]);
            chD.Series.Add(sD);
            host.Add(chD);

            var btnExp = MakeButton("Export details (CSV)", 180);
            btnExp.Click += (s, e) => SaveCsv($"defect_series_{sel}.csv", new[] { "Date", "Occurrences" },
                dates.Select((d, i) => new[] { Lbl(d), yv[i].ToString() }));
            var btnClear = MakeButton("Clear selection", 140);
            btnClear.Click += (s, e) => BeginInvoke(new Action(() => { _selErrSingle = null; SafeRender(_tParetoS); }));
            host.Add(Row(btnExp, btnClear));
        }

        // --- Vue horizontale ---
        if (_chkHoriz.Checked)
        {
            var sorted = items.OrderBy(kv => kv.Value).ToList();
            int h = Math.Min(700, 140 + 28 * sorted.Count);
            var chH = NewChart("Horizontal view (Top N)", "", "Occurrences (deduplicated)", h, allLabels: true);
            var sH = NewSeries("Count", SeriesChartType.Bar, C("#5DADE2"));
            sH.ToolTip = "#AXISLABEL\n#VAL";
            foreach (var kv in sorted) AddPoint(sH, kv.Key, kv.Value, kv.Value.ToString());
            chH.Series.Add(sH);
            host.Add(chH);
        }
    }

    // ---------- Temps de marquage ----------
    private void RenderMarkingSingle()
    {
        var host = _tMark.Content;
        var sb = CurrentSite();
        if (sb == null) { host.Add(Note("No site selected.")); return; }

        var f = FilterRange(sb.MarkingTimes);
        if (f.Count == 0) { host.Add(Note("No marking time data in the selected period.")); return; }

        host.Add(Heading("Average Marking time per day"));
        var chart = NewChart(null, "Date", "Average time (s)", 340);
        var ser = NewSeries("Marking time", SeriesChartType.Column, C("#5DADE2"));
        ser.ToolTip = "#AXISLABEL\n#VAL{N2} s";
        var allMs = new List<double>();
        foreach (var d in Sorted(f.Keys))
        {
            var arr = f[d];
            if (arr.Count > 0)
            {
                var mean = MsToSeconds(arr.Average());
                AddPoint(ser, Lbl(d), mean, mean.ToString("0.00", CultureInfo.InvariantCulture));
                allMs.AddRange(arr);
            }
            else AddPoint(ser, Lbl(d), 0, "-");
        }
        chart.Series.Add(ser);
        host.Add(chart);

        var overall = allMs.Count > 0 ? MsToSeconds(allMs.Average()).ToString("0.00", CultureInfo.InvariantCulture) : "-";
        host.Add(MetricRow(("Overall mean in period (s)", overall)));
    }

    // ---------- Offsets ----------
    private void RenderOffsets()
    {
        var host = _tOff.Content;
        if (_index == null || _cmbSite.SelectedItem is not string site) { host.Add(Note("No site selected.")); return; }

        // --- Journal des changements ---
        host.Add(Heading("Machine offsets - Change log"));
        var changes = _index.OffsetChanges
            .Where(r => r.Site == site && InRange(r.Date))
            .OrderBy(r => r.Site, StringComparer.Ordinal).ThenBy(r => r.Date).ThenBy(r => r.TimeS ?? 1_000_000_000)
            .ToList();

        if (changes.Count == 0)
        {
            host.Add(Note("No offsets change in this period for the selected site."));
        }
        else
        {
            var dt = new DataTable();
            dt.Columns.Add("Site/Machine", typeof(string));
            dt.Columns.Add("Date", typeof(string));
            dt.Columns.Add("Time", typeof(string));
            dt.Columns.Add("Parameter", typeof(string));
            dt.Columns.Add("Old", typeof(double));
            dt.Columns.Add("New", typeof(double));
            dt.Columns.Add("Δ", typeof(double));
            dt.Columns.Add("Δ abs", typeof(double));
            foreach (var c in changes)
                dt.Rows.Add(c.Site, Lbl(c.Date), LogEngine.SecondsToHhmmss(c.TimeS) ?? "-", c.Param, c.Old, c.New, c.Delta, c.AbsDelta);
            host.Add(MakeGrid(dt, 300));

            var btn = MakeButton("Export change log (CSV)", 200);
            btn.Click += (s, e) => SaveCsv("offsets_changes.csv",
                new[] { "Site/Machine", "Date", "Time", "Parameter", "Old", "New", "Delta", "Delta abs" },
                changes.Select(c => new[]
                {
                    c.Site, Lbl(c.Date), LogEngine.SecondsToHhmmss(c.TimeS) ?? "-", c.Param,
                    Num(c.Old), Num(c.New), Num(c.Delta), Num(c.AbsDelta)
                }));
            host.Add(Row(btn));
        }

        // --- Tendance par parametre ---
        host.Add(Heading("Machine offsets - Per-parameter trend"));
        var obs = LogEngine.SortObservations(
            (_index.OffsetObservations.GetValueOrDefault(site) ?? new List<OffsetObs>()).Where(o => InRange(o.Date)));
        if (obs.Count == 0)
        {
            host.Add(Note("No offsets observations in this period."));
            return;
        }

        foreach (var param in LogEngine.OffsetKeys)
        {
            var line = NewSeries(param, SeriesChartType.Line, C("#1F77B4"));
            line.XValueType = ChartValueType.DateTime;
            line.MarkerStyle = MarkerStyle.Circle;
            line.MarkerSize = 6;
            line.ToolTip = "#VALX{dd/MM/yyyy HH:mm:ss}\n#VAL{N3}";
            var marks = NewSeries("Change", SeriesChartType.Point, C("#D62728"));
            marks.XValueType = ChartValueType.DateTime;
            marks.MarkerStyle = MarkerStyle.Diamond;
            marks.MarkerSize = 10;
            marks.ToolTip = "Change\n#VALX{dd/MM/yyyy HH:mm:ss}\n#VAL{N3}";

            double? lastV = null;
            int n = 0;
            foreach (var o in obs)
            {
                if (!o.Offsets.TryGetValue(param, out var v)) continue;
                double x = o.Date.AddSeconds(o.TimeS ?? 0).ToOADate();
                line.Points.AddXY(x, v);
                n++;
                if (lastV != null && v != lastV.Value) marks.Points.AddXY(x, v);
                lastV = v;
            }

            if (n == 0)
            {
                host.Add(Note($"- {param}: no data in this period."));
                continue;
            }

            var chart = NewChart($"{param} - {PeriodText()} - Site: {site}", "Date/Time", param, 320, legend: true);
            var area = chart.ChartAreas[0];
            area.AxisY.IsStartedFromZero = false;
            area.AxisX.LabelStyle.Format = "dd/MM HH:mm";
            chart.Series.Add(line);
            if (marks.Points.Count > 0) chart.Series.Add(marks);
            host.Add(chart);
        }
    }

    // ---------- Export Excel ----------
    private void RenderExport()
    {
        var host = _tExport.Content;
        host.Add(Heading("Data export (per site)"));
        host.Add(Note("The workbook contains every site and the whole log history (the period filter is not applied)."));
        var btn = MakeButton("Download data (Excel, per site)", 260);
        btn.Click += (s, e) => ExportExcel();
        host.Add(Row(btn));
    }

    private void ExportExcel()
    {
        if (_index == null) return;

        var summary = new DataTable("Summary (per site)");
        summary.Columns.Add("Site", typeof(string));
        summary.Columns.Add("Date", typeof(DateTime));
        summary.Columns.Add("Lenses engraved", typeof(int));
        summary.Columns.Add("Checks total", typeof(int));
        summary.Columns.Add("Checks OK", typeof(int));
        summary.Columns.Add("Checks KO", typeof(int));
        summary.Columns.Add("Energy calibrations", typeof(int));
        summary.Columns.Add("Avg marking time (s)", typeof(double));

        var defects = new DataTable("Defects (per site)");
        defects.Columns.Add("Site", typeof(string));
        defects.Columns.Add("Date", typeof(DateTime));
        defects.Columns.Add("Defect", typeof(string));
        defects.Columns.Add("Events (dedup)", typeof(int));

        var ko = new DataTable("KO (per site)");
        ko.Columns.Add("Site", typeof(string));
        ko.Columns.Add("Date", typeof(DateTime));
        ko.Columns.Add("KO reason", typeof(string));
        ko.Columns.Add("Occurrences", typeof(int));

        foreach (var site in _index.SiteNames())
        {
            var sb = _index.Sites[site];
            var dates = new HashSet<DateTime>(sb.Lenses.Keys);
            dates.UnionWith(sb.HealthTotal.Keys);
            dates.UnionWith(sb.HealthOk.Keys);
            dates.UnionWith(sb.HealthKo.Keys);
            dates.UnionWith(sb.CalibrationsPerDate.Keys);
            dates.UnionWith(sb.MarkingTimes.Keys);
            foreach (var d in dates.OrderBy(x => x))
            {
                double? avg = sb.MarkingTimes.TryGetValue(d, out var arr) && arr.Count > 0 ? MsToSeconds(arr.Average()) : null;
                summary.Rows.Add(site, d,
                    sb.Lenses.GetValueOrDefault(d), sb.HealthTotal.GetValueOrDefault(d),
                    sb.HealthOk.GetValueOrDefault(d), sb.HealthKo.GetValueOrDefault(d),
                    sb.CalibrationsPerDate.GetValueOrDefault(d),
                    (object?)avg ?? DBNull.Value);
            }
            foreach (var d in sb.Errors.Keys.OrderBy(x => x))
                foreach (var kv in sb.Errors[d]) defects.Rows.Add(site, d, kv.Key, kv.Value);
            foreach (var d in sb.HealthKoReasons.Keys.OrderBy(x => x))
                foreach (var kv in sb.HealthKoReasons[d]) ko.Rows.Add(site, d, kv.Key, kv.Value);
        }

        var tables = new[] { summary, defects, ko }.Where(t => t.Rows.Count > 0).ToList();
        if (tables.Count == 0)
        {
            MessageBox.Show(this, "There is no data to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "log_analysis_per_site.xlsx" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            foreach (var t in tables)
            {
                var ws = wb.Worksheets.Add(t, t.TableName);
                ws.Columns().AdjustToContents();
            }
            wb.SaveAs(dlg.FileName);
            SetStatus("Saved: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file:\n" + ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ============================================================
    // MODE COMPARISON
    // ============================================================
    private bool CmpGuard(ContentPanel host, out List<string> sites)
    {
        sites = CheckedSites().Where(s => _index!.Sites.ContainsKey(s)).ToList();
        if (sites.Count == 0)
        {
            host.Add(Note("Pick at least one site in the sidebar."));
            return false;
        }
        return true;
    }

    private void RenderCmpSummary()
    {
        var host = _cSummary.Content;
        if (!CmpGuard(host, out var sites)) return;

        host.Add(Heading("Multi-site comparison - Period summary"));
        var dt = new DataTable();
        dt.Columns.Add("Site", typeof(string));
        dt.Columns.Add("Lenses engraved", typeof(int));
        dt.Columns.Add("Checks total", typeof(int));
        dt.Columns.Add("OK", typeof(int));
        dt.Columns.Add("KO", typeof(int));
        dt.Columns.Add("Avg marking time (s)", typeof(string));

        foreach (var s in sites)
        {
            var sb = _index!.Sites[s];
            var dl = SiteDatesInPeriod(sb);
            var mt = dl.SelectMany(d => sb.MarkingTimes.GetValueOrDefault(d) ?? new List<double>()).ToList();
            dt.Rows.Add(s,
                dl.Sum(d => sb.Lenses.GetValueOrDefault(d)),
                dl.Sum(d => sb.HealthTotal.GetValueOrDefault(d)),
                dl.Sum(d => sb.HealthOk.GetValueOrDefault(d)),
                dl.Sum(d => sb.HealthKo.GetValueOrDefault(d)),
                mt.Count > 0 ? MsToSeconds(mt.Average()).ToString("0.00", CultureInfo.InvariantCulture) : "-");
        }
        host.Add(MakeGrid(dt, 360));
    }

    private void RenderCmpHealth()
    {
        var host = _cHealth.Content;
        if (!CmpGuard(host, out var sites)) return;

        host.Add(Heading("Machine Check - comparison (period)"));
        var chart = NewChart(null, "Site", "Checks count", 380, legend: true);
        var sOk = NewSeries("OK (Go=True)", SeriesChartType.StackedColumn, C("#2E8B57"));
        var sKo = NewSeries("KO (Go=False)", SeriesChartType.StackedColumn, C("#DC143C"));
        sOk.ToolTip = sKo.ToolTip = "#SERIESNAME\n#AXISLABEL: #VAL";
        foreach (var s in sites)
        {
            var sb = _index!.Sites[s];
            var dl = SiteDatesInPeriod(sb);
            AddPoint(sOk, s, dl.Sum(d => sb.HealthOk.GetValueOrDefault(d)));
            AddPoint(sKo, s, dl.Sum(d => sb.HealthKo.GetValueOrDefault(d)));
        }
        chart.Series.Add(sOk);
        chart.Series.Add(sKo);
        host.Add(chart);
    }

    private void RenderCmpMarking()
    {
        var host = _cMark.Content;
        if (!CmpGuard(host, out var sites)) return;

        host.Add(Heading("Marking time - daily average (s) - comparison"));
        var perSite = new Dictionary<string, Dictionary<DateTime, double>>();
        var union = new HashSet<DateTime>();
        foreach (var s in sites)
        {
            var sb = _index!.Sites[s];
            var daily = new Dictionary<DateTime, double>();
            foreach (var d in SiteDatesInPeriod(sb))
            {
                var arr = sb.MarkingTimes.GetValueOrDefault(d);
                if (arr != null && arr.Count > 0) daily[d] = MsToSeconds(arr.Average());
            }
            if (daily.Count > 0) { perSite[s] = daily; union.UnionWith(daily.Keys); }
        }
        if (union.Count == 0)
        {
            host.Add(Note("No MarkingTime (daily average) data in the selection/period."));
            return;
        }

        var dates = Sorted(union);
        var chart = NewChart(null, "Date", "Average time (s)", 420, legend: true);
        for (int i = 0; i < sites.Count; i++)
        {
            var ser = NewSeries(sites[i], SeriesChartType.Column, C(Palette[i % Palette.Length]));
            ser.ToolTip = "#SERIESNAME\n#AXISLABEL: #VAL{N2} s";
            var daily = perSite.GetValueOrDefault(sites[i]);
            foreach (var d in dates)
            {
                if (daily != null && daily.TryGetValue(d, out var v))
                    AddPoint(ser, Lbl(d), v, v.ToString("0.00", CultureInfo.InvariantCulture) + "s");
                else
                    AddPoint(ser, Lbl(d), 0, "-");
            }
            chart.Series.Add(ser);
        }
        host.Add(chart);
    }

    private void RenderCmpLenses()
    {
        var host = _cLens.Content;
        if (!CmpGuard(host, out var sites)) return;

        host.Add(Heading("Lenses per day - comparison"));
        var union = new HashSet<DateTime>();
        foreach (var s in sites) union.UnionWith(SiteDatesInPeriod(_index!.Sites[s]));
        if (union.Count == 0) { host.Add(Note("No lenses in the selected period.")); return; }

        var dates = Sorted(union);
        var chart = NewChart(null, "Date", "Lenses / day", 420, legend: true);
        for (int i = 0; i < sites.Count; i++)
        {
            var sb = _index!.Sites[sites[i]];
            var ser = NewSeries(sites[i], SeriesChartType.Column, C(Palette[i % Palette.Length]));
            ser.ToolTip = "#SERIESNAME\n#AXISLABEL: #VAL";
            foreach (var d in dates) AddPoint(ser, Lbl(d), sb.Lenses.GetValueOrDefault(d));
            chart.Series.Add(ser);
        }
        host.Add(chart);
    }

    private void RenderCmpPareto()
    {
        var host = _cPareto.Content;
        if (!CmpGuard(host, out var sites)) return;

        host.Add(Heading("Defects Pareto - multi-site"));
        var q = _txtParetoC.Text.Trim().ToLowerInvariant();
        var errBySite = new Dictionary<string, Dictionary<string, int>>();
        var union = new Dictionary<string, int>();
        foreach (var s in sites)
        {
            var sb = _index!.Sites[s];
            var ctr = SumCounters(SiteDatesInPeriod(sb).Select(d => sb.Errors.GetValueOrDefault(d) ?? new Dictionary<string, int>()));
            if (q.Length > 0)
                ctr = ctr.Where(kv => kv.Key.ToLowerInvariant().Contains(q)).ToDictionary(kv => kv.Key, kv => kv.Value);
            errBySite[s] = ctr;
            foreach (var kv in ctr) union[kv.Key] = union.GetValueOrDefault(kv.Key) + kv.Value;
        }
        if (union.Count == 0) { host.Add(Note("No defect found (or filtered out).")); return; }

        int top = (int)_numTopC.Value;
        var topErrors = union.OrderByDescending(kv => kv.Value).Take(top).Select(kv => kv.Key).ToList();
        bool pct = _chkNorm.Checked;

        var chart = NewChart(null, "Defect", pct ? "%" : "Occurrences", 520, legend: true, xAngle: -45, allLabels: true);
        for (int i = 0; i < sites.Count; i++)
        {
            var ctr = errBySite[sites[i]];
            var ser = NewSeries(sites[i], SeriesChartType.Column, C(Palette[i % Palette.Length]));
            ser.ToolTip = pct
                ? "#SERIESNAME\n#AXISLABEL\n#VAL{N1} %\nClick here for more details"
                : "#SERIESNAME\n#AXISLABEL\n#VAL\nClick here for more details";
            double total = topErrors.Sum(e => (double)ctr.GetValueOrDefault(e));
            foreach (var e in topErrors)
            {
                double y = ctr.GetValueOrDefault(e);
                if (pct) y = total > 0 ? y / total * 100.0 : 0;
                AddPoint(ser, e, y);
            }
            chart.Series.Add(ser);
        }
        AttachClick(chart, name => { _selErrCmp = name; SafeRender(_cPareto); });
        host.Add(chart);

        // --- Detail jour x site du defaut selectionne ---
        var sel = _selErrCmp;
        if (string.IsNullOrEmpty(sel)) return;

        host.Add(Heading($"Selected defect: {sel} - day x site comparison"));
        var perSiteDaily = new Dictionary<string, Dictionary<DateTime, int>>();
        var allDates = new HashSet<DateTime>();
        foreach (var s in sites)
        {
            var sb = _index!.Sites[s];
            var daily = new Dictionary<DateTime, int>();
            foreach (var d in SiteDatesInPeriod(sb))
                daily[d] = sb.Errors.GetValueOrDefault(d)?.GetValueOrDefault(sel) ?? 0;
            perSiteDaily[s] = daily;
            allDates.UnionWith(daily.Keys);
        }
        if (allDates.Count == 0) { host.Add(Note("No occurrence in the selection/period.")); return; }

        var dates = Sorted(allDates);
        var chD = NewChart($"Occurrences per day - {PeriodText()}", "Date", "Occurrences (deduplicated)", 420, legend: true);
        for (int i = 0; i < sites.Count; i++)
        {
            var ser = NewSeries(sites[i], SeriesChartType.Column, C(Palette[i % Palette.Length]));
            ser.ToolTip = "#SERIESNAME\n#AXISLABEL: #VAL";
            foreach (var d in dates) AddPoint(ser, Lbl(d), perSiteDaily[sites[i]].GetValueOrDefault(d));
            chD.Series.Add(ser);
        }
        host.Add(chD);

        var btnExp = MakeButton("Export comparison details (CSV)", 240);
        btnExp.Click += (s, e) => SaveCsv($"defect_series_comparison_{sel}.csv",
            new[] { "Site", "Date", "Occurrences" },
            sites.SelectMany(site => dates.Select(d => new[] { site, Lbl(d), perSiteDaily[site].GetValueOrDefault(d).ToString() })));
        var btnClear = MakeButton("Clear selection", 140);
        btnClear.Click += (s, e) => BeginInvoke(new Action(() => { _selErrCmp = null; SafeRender(_cPareto); }));
        host.Add(Row(btnExp, btnClear));
    }
}

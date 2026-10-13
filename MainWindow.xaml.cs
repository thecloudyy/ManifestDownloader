using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ManifestDownloader;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    // ── theme-aware status colors (frozen, safe to share across threads) ──
    private static readonly Brush OkBrush = Make(0x3F, 0xB9, 0x50);
    private static readonly Brush FailBrush = Make(0xF8, 0x51, 0x49);
    private static readonly Brush WarnBrush = Make(0xD2, 0x99, 0x22);
    private static readonly Brush AccentBrush = Make(0x4C, 0xC2, 0xFF);
    private static readonly Brush MutedBrush = Make(0x8A, 0x8A, 0x8A);
    private static readonly Brush StampBrush = Make(0x77, 0x77, 0x77);

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _libCts;
    private AppSettings _settings = new();
    private readonly ObservableCollection<LibraryGame> _library = new();
    private readonly List<LibraryGame> _libraryAll = new();
    private bool _loadingLib;
    private bool _loadingSettings;
    private DateTime? _runStarted;
    private readonly DispatcherTimer _elapsedTimer;

    // ── queue row: status + per-row progress, marshals change events to the UI thread ──
    private sealed class QueueRow : INotifyPropertyChanged
    {
        public string App { get; }
        public string Game { get; }
        public string Depot { get; }
        public string Manifest { get; }

        private string _status = "queued";
        private Brush _statusBrush = MutedBrush;
        private double _rowProgress;

        public string Status => _status;
        public Brush StatusBrush => _statusBrush;
        public double RowProgress => _rowProgress;

        public QueueRow(string app, string game, string depot, string manifest)
        {
            App = app; Game = game; Depot = depot; Manifest = manifest;
        }

        public void UpdateStatus(string status, Brush brush)
        {
            _status = status;
            _statusBrush = brush;
            Raise(nameof(Status));
            Raise(nameof(StatusBrush));
        }

        public void SetProgress(double percent)
        {
            double value = Math.Clamp(percent, 0, 100);
            if (Math.Abs(value - _rowProgress) < 0.5 && value != 100) return;
            _rowProgress = value;
            Raise(nameof(RowProgress));
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler is null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.InvokeAsync(() => handler(this, new PropertyChangedEventArgs(name)));
            else
                handler(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public MainWindow()
    {
        InitializeComponent();
        LibraryBox.ItemsSource = _library;

        LoadPersistedSettings();
        PrefillFromEnvironment();
        UpdateModeUi();
        UpdateSteamStatus();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_runStarted is { } started)
                LblElapsed.Text = (DateTime.Now - started).ToString(@"mm\:ss");
        };

        Log("Ready. Pick a source, enter an AppID, hit Download.");
        Loaded += async (_, _) => await LoadLibraryAsync();
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); _libCts?.Cancel(); } catch { }
            SaveWindowSize();
            PersistSettings();
        };
    }

    private ManifestMode Mode =>
        RbMorrenus.IsChecked == true ? ManifestMode.GitHubMorrenus :
        RbManifestHub.IsChecked == true ? ManifestMode.GitHubManifestHub : ManifestMode.GitHub;

    /// <summary>The currently enabled sources, in attempt order:
    /// GitHub mirror → ManifestDeX → fallback API.</summary>
    private DownloadSources Sources => new(
        Mode,
        TglGithub.IsChecked == true,
        TglDeX.IsChecked == true);

    /// <summary>Only the Morrenus/ManifestHub fallbacks need an API key —
    /// the GitHub mirror and the ManifestDeX code API are keyless.</summary>
    private bool NeedsApiKey => Mode is ManifestMode.GitHubMorrenus or ManifestMode.GitHubManifestHub;

    private string ApiKey => TxtKeyPass.Password.Trim();

    // ── UI events ──

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateModeUi();

    private static string KeyUrlFor(ManifestMode mode, bool manifestDeX) => mode switch
    {
        ManifestMode.GitHubMorrenus => "https://hubcapmanifest.com/api-keys/user",
        ManifestMode.GitHubManifestHub => "https://manifesthub1.filegear-sg.me/",
        _ => manifestDeX ? ManifestService.ManifestDeXDocsUrl
                         : "https://github.com/qwe213312/k25FCdfEOoEJ42S6",
    };

    private void BtnGetKey_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(KeyUrlFor(Mode, TglDeX.IsChecked == true)) { UseShellExecute = true }); }
        catch (Exception ex) { Log($"Can't open the key page: {ex.Message}", FailBrush); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? steamDir = ManifestService.DetectSteamDir();
        if (steamDir is null) { Log("Steam not found.", WarnBrush); return; }
        try
        {
            string dir = Path.Combine(steamDir, "depotcache");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"Can't open folder: {ex.Message}", FailBrush); }
    }

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var range = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
            if (string.IsNullOrWhiteSpace(range.Text)) { Log("Log is empty."); return; }
            Clipboard.SetText(range.Text);
            Log("Log copied to clipboard.", OkBrush);
        }
        catch (Exception ex) { Log($"Copy failed: {ex.Message}", FailBrush); }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        Log("Log cleared.");
    }

    // ── keyboard shortcuts (window-level) ──

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            if (BtnRefreshLib.IsEnabled) _ = LoadLibraryAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && BtnCancel.IsEnabled)
        {
            _cts?.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            TxtLibSearch.Focus();
            TxtLibSearch.SelectAll();
            e.Handled = true;
        }
    }

    private async void TxtAppId_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunDownloadAsync();
    }

    // ── Persisted settings (mode, last AppID, theme, window size, DPAPI key) ──

    private void LoadPersistedSettings()
    {
        _settings = SettingsStore.Load();
        _loadingSettings = true; // applying sources fires events; don't save mid-load
        try
        {
        if (_settings.UseGithub is { } ug) TglGithub.IsChecked = ug;
        if (_settings.UseManifestDeX is { } ud) TglDeX.IsChecked = ud;
        if (_settings.ApiSource is { } api)
            ApplyApiRadio(api);
        else if (_settings.UseGithub is null)
            ApplySourceTokens(_settings.Mode); // legacy file: derive toggles from Mode
        else if (_settings.Mode == "github+morrenus") RbMorrenus.IsChecked = true;
        else if (_settings.Mode == "github+manifesthub") RbManifestHub.IsChecked = true;
        if (_settings.RememberKey && SettingsStore.Unprotect(_settings.ProtectedKey) is { } savedKey)
            TxtKeyPass.Password = savedKey;
        ChkRememberKey.IsChecked = _settings.RememberKey;
        if (!string.IsNullOrWhiteSpace(_settings.LastAppId))
            TxtAppId.Text = _settings.LastAppId;
        }
        finally { _loadingSettings = false; }

        if (_settings.WindowWidth is >= 980 and <= 6000) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight is >= 640 and <= 4000) Height = _settings.WindowHeight.Value;
    }

    private void PersistSettings()
    {
        _settings.UseGithub = TglGithub.IsChecked == true;
        _settings.UseManifestDeX = TglDeX.IsChecked == true;
        _settings.ApiSource = Mode switch
        {
            ManifestMode.GitHubMorrenus => "morrenus",
            ManifestMode.GitHubManifestHub => "manifesthub",
            _ => "none",
        };
        // Legacy Mode string, best effort for older builds reading this file.
        var parts = new List<string>();
        if (_settings.UseGithub.Value) parts.Add("github");
        if (_settings.UseManifestDeX.Value) parts.Add("manifestdex");
        if (_settings.ApiSource != "none") parts.Add(_settings.ApiSource!);
        _settings.Mode = parts.Count > 0 ? string.Join("+", parts) : "none";
        _settings.RememberKey = ChkRememberKey.IsChecked == true;
        _settings.ProtectedKey = _settings.RememberKey ? SettingsStore.Protect(ApiKey) : null;
        if (Regex.IsMatch(TxtAppId.Text.Trim(), @"^\d+$"))
            _settings.LastAppId = TxtAppId.Text.Trim();
        SettingsStore.Save(_settings);
    }

    private void SaveWindowSize()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
    }

    private void RememberKey_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtKeyPass is null || TxtAppId is null) return; // still initializing
        if (ChkRememberKey.IsChecked != true)
        {
            _settings.RememberKey = false;
            _settings.ProtectedKey = null;
            SettingsStore.Save(_settings);
            Log("Saved API key cleared.");
        }
        else PersistSettings();
    }

    private void UpdateSteamStatus()
    {
        try
        {
            string? steamDir = ManifestService.DetectSteamDir();
            LblSteamPath.Text = steamDir is null ? "Steam: not found" : $"Steam: {steamDir}";
            LblSteamPath.Foreground = steamDir is null ? WarnBrush : Brushes.Gray;
        }
        catch { LblSteamPath.Text = "Steam: unknown"; }
    }

    // ── Library (Element-style selection) ──

    private sealed class LibraryGame : INotifyPropertyChanged
    {
        public string AppId { get; }
        public string Name { get; }
        public int Depots { get; }
        private Stretch _thumbStretch = Stretch.UniformToFill;
        /// <summary>Fill-crop normally; native size for tiny icons so they stay sharp.</summary>
        public Stretch ThumbStretch
        {
            get => _thumbStretch;
            set { _thumbStretch = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbStretch))); }
        }
        private ImageSource? _cover;
        public ImageSource? Cover
        {
            get => _cover;
            set { _cover = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover))); }
        }
        public LibraryGame(string appId, string name, int depots) { AppId = appId; Name = name; Depots = depots; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private async Task LoadLibraryAsync()
    {
        if (_loadingLib) return;
        _loadingLib = true;
        BtnRefreshLib.IsEnabled = false;
        try
        {
            // Long-lived token: the cover prefetch outlives this method, so the
            // CTS must not be disposed here (that silently killed in-flight covers).
            _libCts?.Cancel();
            _libCts?.Dispose();
            _libCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var ct = _libCts.Token;
            string? steamDir = ManifestService.DetectSteamDir();
            if (steamDir is null) { Log("Library: Steam not found.", WarnBrush); UpdateSteamStatus(); return; }
            UpdateSteamStatus();
            string plugIn = Path.Combine(steamDir, "config", "stplug-in");
            var entries = LibraryService.EnumerateInstalled(plugIn).ToList();
            if (entries.Count == 0) { Log("Library: no installed luas."); ApplyLibFilter(); return; }
            var applist = await LibraryService.GetAppListAsync(ct);
            _libraryAll.Clear();
            _libraryAll.AddRange(entries
                .OrderBy(e => LibraryService.ResolveName(e.AppId, e.Path, applist))
                .Select(e => new LibraryGame(e.AppId.ToString(),
                    LibraryService.ResolveName(e.AppId, e.Path, applist),
                    ManifestService.GetDepotIdsFromLua(e.Path).Count)));
            ApplyLibFilter();
            Log($"Library: {entries.Count} game(s).");
            _ = PrefetchCoversAsync(ct);
        }
        catch (Exception ex)
        {
            Log($"Library failed: {ex.Message}", WarnBrush);
        }
        finally
        {
            _loadingLib = false;
            BtnRefreshLib.IsEnabled = BtnDownload.IsEnabled;
        }
    }

    private async Task PrefetchCoversAsync(CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = _libraryAll.Select(async g =>
        {
            bool acquired = false;
            try
            {
                await gate.WaitAsync(ct);
                acquired = true;
                if (!long.TryParse(g.AppId, out long id)) return;
                string? path = await LibraryService.EnsureCoverAsync(id, ct);
                path ??= await LibraryService.EnsureCommunityIconAsync(id, ct);
                path ??= LibraryService.GeneratePlaceholder(id, g.Name);
                if (path is null) return;
                ImageSource? img = TryLoadCover(path);
                if (img is null)
                {
                    try { File.Delete(path); } catch { }
                    path = LibraryService.GeneratePlaceholder(id, g.Name);
                    img = path is null ? null : TryLoadCover(path);
                }
                if (img is null) return;
                if (img is BitmapImage bmp && bmp.PixelWidth < 92)
                    g.ThumbStretch = Stretch.None;
                g.Cover = img;
            }
            catch { }
            finally { if (acquired) gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch { }
    }

    private static ImageSource? TryLoadCover(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 184;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void ApplyLibFilter()
    {
        string q = TxtLibSearch.Text.Trim();
        _library.Clear();
        foreach (var g in _libraryAll.Where(g =>
                     q.Length == 0 ||
                     g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     g.AppId.Contains(q, StringComparison.OrdinalIgnoreCase)))
            _library.Add(g);
        LblLibEmpty.Text = _libraryAll.Count == 0
            ? "No games found — is Steam installed?"
            : "No games match your search.";
        LblLibEmpty.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibStatus();
    }

    private void TxtLibSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyLibFilter();

    private void LibraryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is LibraryGame g)
            TxtAppId.Text = g.AppId;
        UpdateLibStatus();
    }

    private void UpdateLibStatus()
    {
        int n = LibraryBox.SelectedItems.Count;
        int total = _libraryAll.Count;
        int shown = _library.Count;
        LblLibStatus.Text = shown == total
            ? $"{total} games • {n} selected"
            : $"{n} selected • showing {shown} of {total}";
        BtnDownload.Content = n > 0 ? $"Download ({n})" : "Download";
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => LibraryBox.SelectAll();

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e) => LibraryBox.UnselectAll();

    private void LibraryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            LibraryBox.SelectAll();
            e.Handled = true;
        }
    }

    private async void LibraryBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryBox.SelectedItem is not LibraryGame g || !BtnDownload.IsEnabled) return;
        LibraryBox.UnselectAll();
        LibraryBox.SelectedItem = g;
        TxtAppId.Text = g.AppId;
        await RunDownloadAsync();
    }

    private async void BtnRefreshLib_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

    private void UpdateModeUi()
    {
        if (TxtKeyPass is null) return; // still initializing
        bool needsKey = NeedsApiKey;
        bool deX = TglDeX.IsChecked == true;
        TxtKeyPass.IsEnabled = needsKey && BtnDownload.IsEnabled;
        if (BtnGetKey is not null)
        {
            BtnGetKey.IsEnabled = needsKey || deX;
            BtnGetKey.Content = needsKey ? "Get key" : "Docs";
            BtnGetKey.ToolTip = needsKey
                ? "Open the key page in your browser"
                : "Open the ManifestDeX API docs";
        }
        var names = EnabledSourceNames();
        LblModeHint.Text = names.Count == 0
            ? "No source enabled — turn on GitHub, ManifestDeX, or a fallback API."
            : string.Join(" → ", names) + " (attempt order).";
        LblMode.Text = names.Count > 0 ? $"Source: {string.Join(" + ", names)}" : "Source: none";
        if (!_loadingSettings) PersistSettings();
    }

    private List<string> EnabledSourceNames()
    {
        var names = new List<string>();
        if (TglGithub.IsChecked == true) names.Add("GitHub");
        if (TglDeX.IsChecked == true) names.Add("ManifestDeX");
        if (Mode == ManifestMode.GitHubMorrenus) names.Add("Morrenus");
        else if (Mode == ManifestMode.GitHubManifestHub) names.Add("ManifestHub");
        return names;
    }

    /// <summary>Parse a source spec like "github+manifestdex+morrenus",
    /// "manifestdex" or "github,manifesthub" into the toggles/radios.
    /// Used for legacy settings and the MANIFEST_MODE env var.</summary>
    private void ApplySourceTokens(string spec)
    {
        var tokens = spec.Split(new[] { '+', ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return;
        bool github = false, dex = false;
        string api = "none";
        bool recognized = false;
        foreach (string raw in tokens)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "github": github = true; recognized = true; break;
                case "manifestdex": case "manifest-dex": case "manifest_dex": case "dex": dex = true; recognized = true; break;
                case "morrenus": api = "morrenus"; recognized = true; break;
                case "manifesthub": api = "manifesthub"; recognized = true; break;
                case "none": api = "none"; recognized = true; break;
            }
        }
        // An unrecognized spec (e.g. MANIFEST_MODE=fnord) must not silently
        // disable every source — keep the current selection instead.
        if (!recognized) return;
        TglGithub.IsChecked = github;
        TglDeX.IsChecked = dex;
        ApplyApiRadio(api);
    }

    private void ApplyApiRadio(string api)
    {
        if (api == "morrenus") RbMorrenus.IsChecked = true;
        else if (api == "manifesthub") RbManifestHub.IsChecked = true;
        else RbApiNone.IsChecked = true;
    }

    private void Source_Changed(object sender, RoutedEventArgs e) => UpdateModeUi();

    private void PrefillFromEnvironment()
    {
        string? mode = Environment.GetEnvironmentVariable("MANIFEST_MODE");
        if (!string.IsNullOrWhiteSpace(mode)) ApplySourceTokens(mode);
        string? key = Environment.GetEnvironmentVariable("MORRENUS_API_KEY")
            ?? Environment.GetEnvironmentVariable("MH_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) TxtKeyPass.Password = key.Trim();
        string? app = Environment.GetEnvironmentVariable("APP_ID");
        if (!string.IsNullOrWhiteSpace(app)) TxtAppId.Text = app.Trim();
    }

    // ── Reinstall: pull the live release from GitHub and replace this copy ──

    private async void BtnReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnDownload.IsEnabled)
        {
            Log("Reinstall: wait for the current download to finish first.", WarnBrush);
            return;
        }
        var answer = MessageBox.Show(
            "Download the latest release from GitHub and replace this copy of ManifestDownloader?\n\n" +
            "The app will close, be replaced, then restart.",
            "Reinstall",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log("Reinstall: cannot locate the running executable.", FailBrush);
            return;
        }

        const string url =
            "https://github.com/thecloudyy/ManifestDownloader/releases/latest/download/ManifestDownloader.exe";
        string tmp = exe + ".new";
        BtnReinstall.IsEnabled = false;
        try
        {
            Log("Reinstall: downloading the latest release...");
            byte[] bytes = await ManifestService.Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(tmp, bytes);
            Log("Reinstall: download complete, replacing and restarting...");

            // The running exe is locked until this process exits: hand the swap to a
            // helper that waits for our PID, moves the new file into place, relaunches.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id {Environment.ProcessId}; " +
                    $"Move-Item -LiteralPath '{tmp}' -Destination '{exe}' -Force; Start-Process -FilePath '{exe}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log("Reinstall failed: " + ex.Message, FailBrush);
            try { File.Delete(tmp); } catch { }
            BtnReinstall.IsEnabled = true;
        }
    }

    // ── Download flow (same steps as the script, N depots in parallel) ──

    private async void BtnDownload_Click(object sender, RoutedEventArgs e) => await RunDownloadAsync();

    private sealed record AppResolution(
        string AppId, string Name, List<DepotJob> Jobs, List<string> Missing, string? Error);

    private async Task RunDownloadAsync()
    {
        if (!BtnDownload.IsEnabled) return; // already running (e.g. double-click during a run)
        // Multi-select: download every selected library game; otherwise the typed AppID.
        var appIds = LibraryBox.SelectedItems.Cast<LibraryGame>().Select(g => g.AppId).Distinct().ToList();
        if (appIds.Count == 0)
        {
            string typed = TxtAppId.Text.Trim();
            if (!Regex.IsMatch(typed, @"^\d+$"))
            {
                Log("Select game(s) in the library or enter a numeric AppID.", FailBrush);
                return;
            }
            appIds.Add(typed);
        }
        var sources = Sources;
        if (!sources.AnyEnabled)
        {
            Log("No download source enabled — turn on GitHub, ManifestDeX, or a fallback API.", FailBrush);
            return;
        }
        string apiKey = ApiKey;
        if (NeedsApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            Log("This mode needs an API key.", FailBrush);
            return;
        }
        if (Mode == ManifestMode.GitHubMorrenus && !ManifestService.IsValidMorrenusFormat(apiKey))
        {
            Log("Invalid Morrenus key format: expected smm_ + 96 hex chars.", FailBrush);
            return;
        }
        PersistSettings();

        SetRunning(true);
        QueueView.Items.Clear();
        ResultBar.IsOpen = false;
        LblSummary.Text = "";
        LblStatus.Text = "Starting…";
        LblPercent.Text = "0%";
        ProgOverall.Value = 0;
        LblElapsed.Text = "00:00";
        _runStarted = DateTime.Now;
        _elapsedTimer.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var started = DateTime.Now;

        // Cancel/error must not leave rows frozen at "queued"/"downloading…".
        void MarkPendingRows(string status, Brush brush)
        {
            foreach (QueueRow row in QueueView.Items)
                if (row.Status is "queued" or "downloading…")
                    row.UpdateStatus(status, brush);
        }

        try
        {
            Log("Locating Steam...");
            string? steamDir = ManifestService.DetectSteamDir();
            if (steamDir is null) { Log("Could not find Steam installation.", FailBrush); LblStatus.Text = "Steam not found."; return; }
            Log($"Steam: {steamDir}", OkBrush);

            if (Mode == ManifestMode.GitHubMorrenus)
            {
                Log("Validating Morrenus key...");
                var (ok, msg) = await ManifestService.ValidateMorrenusKeyAsync(apiKey, ct);
                Log(msg, ok ? OkBrush : FailBrush);
                if (!ok) { LblStatus.Text = "API key rejected."; return; }
            }

            bool multi = appIds.Count > 1;
            var names = _libraryAll.ToDictionary(g => g.AppId, g => g.Name, StringComparer.Ordinal);

            // Resolve every app's depots concurrently — one SteamCMD round-trip each.
            var resolutions = new ConcurrentDictionary<string, AppResolution>();
            await Task.WhenAll(appIds.Select(appId =>
                ResolveAppAsync(appId, steamDir, multi, names, resolutions, ct)));

            var all = new List<(string App, string Name, DepotJob Job)>();
            var noManifest = new List<(string App, string Name, string Depot)>();
            foreach (string appId in appIds)
            {
                ct.ThrowIfCancellationRequested();
                if (!resolutions.TryGetValue(appId, out var r) || r.Error is not null) continue;
                noManifest.AddRange(r.Missing.Select(d => (r.AppId, r.Name, d)));
                all.AddRange(r.Jobs.Select(j => (r.AppId, r.Name, j)));
            }
            if (all.Count == 0)
            {
                Log("Nothing to download.", WarnBrush);
                LblStatus.Text = "Nothing to download.";
                ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
                ResultBar.Title = "Nothing to download";
                ResultBar.Message = "No public manifests matched the depots of the selected game(s).";
                ResultBar.IsOpen = true;
                return;
            }

            string depotCache = Path.Combine(steamDir, "depotcache");
            Directory.CreateDirectory(depotCache);

            var rows = all.Select(t => new QueueRow(t.App, t.Name, t.Job.DepotId, t.Job.ManifestId)).ToList();
            foreach (var row in rows) QueueView.Items.Add(row);
            foreach (var (app, name, depot) in noManifest)
            {
                var row = new QueueRow(app, name, depot, "-");
                row.UpdateStatus("unavailable", WarnBrush);
                QueueView.Items.Add(row);
            }
            LblQueueCount.Text = noManifest.Count > 0
                ? $"{all.Count} depot(s) queued • {noManifest.Count} unavailable"
                : $"{all.Count} depot(s) queued";

            int doneCount = 0, okCount = 0, activeCount = 0;
            long totalBytes = 0;
            var failed = new ConcurrentBag<(string App, string Depot, string? Error)>();
            int totalDepots = all.Count;
            int parallel = Math.Clamp((all.Count + 1) / 2, 1, 4);
            var gate = new SemaphoreSlim(parallel);

            // UI-thread file progress: aggregate line + per-row bar.
            void OnFileProgress(QueueRow row, string depotId, long done, long? total, double bytesPerSec)
            {
                void Apply()
                {
                    int d = Volatile.Read(ref doneCount);
                    int a = Volatile.Read(ref activeCount);
                    string speed = bytesPerSec > 1024 ? $" @ {FormatSize((long)bytesPerSec)}/s" : "";
                    LblStatus.Text = total is > 0
                        ? $"{d}/{totalDepots} done · {a} active — depot {depotId}: {FormatSize(done)}/{FormatSize(total!.Value)} ({100.0 * done / total.Value:F0}%){speed}"
                        : $"{d}/{totalDepots} done · {a} active — depot {depotId}: {FormatSize(done)}{speed}";
                    if (total is > 0) row.SetProgress(100.0 * done / total.Value);
                }
                if (Dispatcher.CheckAccess()) Apply();
                else Dispatcher.InvokeAsync(Apply);
            }

            var work = all.Zip(rows).ToList();
            var tasks = work.Select(async item =>
            {
                var (app, name, job) = item.First;
                var row = item.Second;
                await gate.WaitAsync(ct);
                Interlocked.Increment(ref activeCount);
                var sw = Stopwatch.StartNew();
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string tag = multi ? $"[{app}] " : "";
                    row.UpdateStatus("downloading…", AccentBrush);
                    SetOverall(Volatile.Read(ref doneCount), totalDepots);

                    double lastSpeed = 0;
                    var prog = new Progress<(long Done, long? Total)>(p =>
                    {
                        double s = sw.Elapsed.TotalSeconds;
                        if (s > 0.25 && p.Done > 0) lastSpeed = p.Done / s;
                        OnFileProgress(row, job.DepotId, p.Done, p.Total, lastSpeed);
                    });

                    var result = await ManifestService.DownloadManifestAsync(
                        job, depotCache, sources, apiKey, prog, ct);

                    if (result.Success)
                    {
                        Interlocked.Increment(ref okCount);
                        Interlocked.Add(ref totalBytes, result.Size);
                        row.SetProgress(100);
                        row.UpdateStatus($"ok ({FormatSize(result.Size)})", OkBrush);
                        Log($"{tag}[+] Depot {job.DepotId} downloaded ({FormatSize(result.Size)}).", OkBrush);
                    }
                    else
                    {
                        failed.Add((app, job.DepotId, result.Error));
                        row.UpdateStatus("failed", FailBrush);
                        Log($"{tag}[-] Depot {job.DepotId} failed: {result.Error}", FailBrush);
                    }
                }
                finally
                {
                    gate.Release();
                    Interlocked.Decrement(ref activeCount);
                    if (!ct.IsCancellationRequested)
                    {
                        int d = Interlocked.Increment(ref doneCount);
                        SetOverall(d, totalDepots);
                    }
                }
            });
            await Task.WhenAll(tasks);

            var elapsed = DateTime.Now - started;
            int okc = Volatile.Read(ref okCount);
            int failc = failed.Count;
            long bytesDone = Interlocked.Read(ref totalBytes);
            string summary = $"Done: {okc} ok, {failc} failed " +
                             $"({FormatSize(bytesDone)}, {elapsed:mm\\:ss}). → {depotCache}";
            LblSummary.Text = $"{okc} ok / {failc} fail";
            LblStatus.Text = failc > 0
                ? $"{failc} depot(s) failed — see the log for details."
                : "All depots downloaded.";
            Log(summary, failc > 0 ? FailBrush : OkBrush);
            ResultBar.Severity = failc > 0
                ? Wpf.Ui.Controls.InfoBarSeverity.Error
                : Wpf.Ui.Controls.InfoBarSeverity.Success;
            ResultBar.Title = failc > 0 ? "Completed with failures" : "Download complete";
            ResultBar.Message = $"{okc} ok, {failc} failed. {FormatSize(bytesDone)} in {elapsed:mm\\:ss}.";
            ResultBar.IsOpen = true;
            foreach (var (app, depot, err) in failed)
                Log($"    {(multi ? $"[{app}] " : "")}Depot {depot}: {err}", FailBrush);
        }
        catch (OperationCanceledException)
        {
            MarkPendingRows("cancelled", WarnBrush);
            Log("Cancelled.", WarnBrush);
            LblStatus.Text = "Cancelled.";
            LblSummary.Text = "cancelled";
        }
        catch (Exception ex)
        {
            MarkPendingRows("error", FailBrush);
            Log($"Error: {ex.Message}", FailBrush);
            LblStatus.Text = "Error — see the log.";
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = "Download failed";
            ResultBar.Message = ex.Message;
            ResultBar.IsOpen = true;
        }
        finally
        {
            _elapsedTimer.Stop();
            _runStarted = null;
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    /// <summary>Parse one game's lua + public manifests. Safe off the UI thread
    /// (every log call and row update marshals itself).</summary>
    private async Task<AppResolution> ResolveAppAsync(
        string appId, string steamDir, bool multi,
        IReadOnlyDictionary<string, string> names,
        ConcurrentDictionary<string, AppResolution> sink, CancellationToken ct)
    {
        AppResolution Fail(string? error, string? logMessage = null)
        {
            if (logMessage is not null)
                Log(logMessage, FailBrush);
            var r = new AppResolution(appId, names.GetValueOrDefault(appId, $"App {appId}"),
                new List<DepotJob>(), new List<string>(), error);
            sink[appId] = r;
            return r;
        }

        string tag = multi ? $"[{appId}] " : "";
        try
        {
            ct.ThrowIfCancellationRequested();
            string luaPath = Path.Combine(steamDir, "config", "stplug-in", $"{appId}.lua");
            if (!File.Exists(luaPath))
                return Fail("no lua", $"{tag}No lua for AppID {appId}.");

            var depotIds = ManifestService.GetDepotIdsFromLua(luaPath);
            if (depotIds.Count == 0)
                return Fail("no depots", $"{tag}No depot IDs in lua.");

            string name = names.GetValueOrDefault(appId)
                ?? LibraryService.ParseLuaName(luaPath)
                ?? $"App {appId}";

            Log($"{tag}Fetching app info...");
            using var appInfo = await ManifestService.GetAppInfoAsync(appId, ct);
            if (appInfo is null)
                return Fail("appinfo failed", $"{tag}SteamCMD API failed.");

            var jobs = depotIds
                .Select(d => (Depot: d, Manifest: ManifestService.GetManifestId(appInfo, appId, d)))
                .Where(j => j.Manifest is not null)
                .Select(j => new DepotJob(j.Depot, j.Manifest!))
                .ToList();
            var matched = new HashSet<string>(jobs.Select(j => j.DepotId));
            var missing = depotIds.Where(d => !matched.Contains(d)).ToList();
            if (missing.Count > 0)
                Log($"{tag}{missing.Count} depot(s) unavailable (no public manifest), skipped: {string.Join(", ", missing)}.");
            if (jobs.Count > 0)
                Log($"{tag}{jobs.Count} depot(s) to fetch.");
            else
                Log($"{tag}No manifests matched any depot.", WarnBrush);

            var r = new AppResolution(appId, name, jobs, missing, null);
            sink[appId] = r;
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail("error", $"{tag}AppID {appId}: {ex.Message}");
        }
    }

    private void SetRunning(bool running)
    {
        BtnDownload.IsEnabled = !running;
        BtnCancel.IsEnabled = running;
        RbApiNone.IsEnabled = !running;
        RbMorrenus.IsEnabled = !running;
        RbManifestHub.IsEnabled = !running;
        TglGithub.IsEnabled = !running;
        TglDeX.IsEnabled = !running;
        ChkRememberKey.IsEnabled = !running;
        TxtKeyPass.IsEnabled = !running && NeedsApiKey;
        if (BtnGetKey is not null)
            BtnGetKey.IsEnabled = !running && (NeedsApiKey || TglDeX.IsChecked == true);
        // Swapping the exe mid-download would kill the run: reinstall only while idle.
        BtnReinstall.IsEnabled = !running;
        if (!_loadingLib) BtnRefreshLib.IsEnabled = !running;
        if (running) LibBusy(true); else LibBusy(false);
    }

    private void LibBusy(bool busy)
    {
        // Keep the library clickable during a run (selection still updates the
        // AppID box) but make the empty-state text honest.
        LblLibEmpty.Visibility = busy || _library.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetOverall(int done, int total)
    {
        void Apply()
        {
            ProgOverall.Maximum = Math.Max(total, 1);
            ProgOverall.Value = Math.Min(Math.Max(done, 0), ProgOverall.Maximum);
            LblPercent.Text = total > 0 ? $"{100.0 * Math.Min(done, total) / total:F0}%" : "0%";
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.InvokeAsync(Apply);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):N2} MB" :
        bytes >= 1024 ? $"{bytes / 1024.0:N2} KB" : $"{bytes} B";

    private void Log(string message, Brush? brush = null)
    {
        void Write()
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ")
            {
                Foreground = StampBrush,
            });
            var run = new Run($"  {message}");
            if (brush is null)
                run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            else
                run.Foreground = brush;
            paragraph.Inlines.Add(run);

            var blocks = LogBox.Document.Blocks;
            blocks.Add(paragraph);
            // Bound memory on long multi-game runs.
            while (blocks.Count > 1000)
                blocks.Remove(blocks.FirstBlock);
            LogBox.ScrollToEnd();
        }

        if (Dispatcher.CheckAccess()) Write();
        else Dispatcher.InvokeAsync(Write);
    }
}

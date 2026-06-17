using SnapStakMobile.Models;
using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class ScanPage : ContentPage
{
    private readonly AppScannerService _scanner;
    private readonly AppDatabaseService _db;
    private readonly AssetExtractorService _extractor = new();

    private readonly List<AppItem> _detectedApps = new();

    public ScanPage(AppScannerService scanner, AppDatabaseService db)
    {
        InitializeComponent();
        _scanner = scanner;
        _db = db;
        AppCollection.ItemsSource = _detectedApps;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        StartScanAsync();
    }

    private void OnBackClicked(object sender, TappedEventArgs e)
        => Navigation.PopAsync();

    // ── Scan ──────────────────────────────────────────────────────────────────

    private async void StartScanAsync()
    {
        _detectedApps.Clear();
        _lastReportedProcessed = -1;
        AppCollection.IsVisible = false;
        EmptyState.IsVisible = false;
        SearchContainer.IsVisible = false;
        ResultsHeader.IsVisible = false;
        DetectedBadge.IsVisible = false;
        ScanSpinner.IsRunning = true;
        ScanSpinner.IsVisible = true;
        ProgressBar.Progress = 0;

        InspectedCountLabel.Text = "0";
        SkippedCountLabel.Text = "0";
        TriagedCountLabel.Text = "0";
        FoundCountLabel.Text = "0";
        ScanPhaseLabel.Text = "Preparing scan...";
        StatusLabel.Text = "Loading installed apps";
        ScanCountLabel.Text = "0";

        try
        {
            // Load timestamps of every previously scanned app
            var scannedTimestamps = await _db.GetScannedTimestampsAsync();
            bool isFirstScan = scannedTimestamps.Count == 0;

            ScanPhaseLabel.Text = isFirstScan ? "Scanning all apps" : "Checking for changes";
            StatusLabel.Text = isFirstScan
                ? "First scan — inspecting every installed app"
                : $"{scannedTimestamps.Count} previously scanned — checking for new or updated apps";

            var progress = new Progress<ScanProgress>(OnProgressUpdate);

            var stats = await _scanner.GetWebViewAppsAsync(
                scannedTimestamps: scannedTimestamps,
                progress: progress,
                onDetected: OnAppDetected);

            // Batch-save all newly inspected app timestamps
            if (stats.ToRecord.Count > 0)
                await _db.MarkScannedBatchAsync(stats.ToRecord);

            // ── Complete ───────────────────────────────────────────────────────
            ScanSpinner.IsRunning = false;
            ScanSpinner.IsVisible = false;
            ProgressBar.Progress = 1.0;
            ScanCountLabel.Text = "";

            InspectedCountLabel.Text = stats.Inspected.ToString();
            SkippedCountLabel.Text = stats.Skipped.ToString();
            TriagedCountLabel.Text = stats.Triaged.ToString();
            FoundCountLabel.Text = stats.Found.ToString();

            if (stats.Found == 0)
            {
                ScanPhaseLabel.Text = "Scan complete";
                StatusLabel.Text = stats.Skipped > 0
                    ? $"No new apps found — {stats.Skipped} unchanged apps skipped"
                    : "No WebView apps found on this device";

                EmptyState.IsVisible = true;
                EmptyTitleLabel.Text = stats.Skipped > 0 ? "No New Apps Found" : "No WebView Apps Found";
                EmptySubLabel.Text = stats.Skipped > 0
                    ? $"All {stats.Skipped} previously scanned apps are unchanged."
                    : "No Capacitor, Cordova, Base44, Lovable or Median apps were detected.";
            }
            else
            {
                ScanPhaseLabel.Text = "Scan complete";
                StatusLabel.Text = $"{stats.Found} new app{(stats.Found == 1 ? "" : "s")} added to your library";
                SearchContainer.IsVisible = true;
                ResultsHeader.IsVisible = true;
                ResultsCountLabel.Text = $"{stats.Found} app{(stats.Found == 1 ? "" : "s")}";
            }
        }
        catch (Exception ex)
        {
            ScanSpinner.IsRunning = false;
            ScanSpinner.IsVisible = false;
            ScanPhaseLabel.Text = "Scan failed";
            StatusLabel.Text = ex.Message;
            EmptyState.IsVisible = true;
        }
    }

    // ── Progress updates ──────────────────────────────────────────────────────

    private int _lastReportedProcessed = -1;

    private void OnProgressUpdate(ScanProgress p)
    {
        // Only post to the main thread every 10 apps, or when an app is found,
        // or when the phase changes. Flooding MainThread.BeginInvokeOnMainThread
        // on every single skip/triage causes continuous bitmap compression on
        // the UI thread and ANR crashes on large app corpora.
        int processed = p.Inspected + p.Skipped + p.Triaged;
        bool shouldUpdate = p.Phase == ScanPhase.Found
                         || p.Phase == ScanPhase.Inspecting
                         || (processed - _lastReportedProcessed) >= 10;

        if (!shouldUpdate) return;
        _lastReportedProcessed = processed;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            InspectedCountLabel.Text = p.Inspected.ToString();
            SkippedCountLabel.Text = p.Skipped.ToString();
            TriagedCountLabel.Text = p.Triaged.ToString();
            FoundCountLabel.Text = p.Found.ToString();

            if (p.Phase == ScanPhase.Skipping)
            {
                ScanPhaseLabel.Text = "Skipping unchanged apps";
                StatusLabel.Text = $"{p.Skipped} skipped so far...";
            }
            else if (p.Phase == ScanPhase.Triaging)
            {
                ScanPhaseLabel.Text = "Filtering native apps";
                StatusLabel.Text = $"{p.Triaged} native apps filtered";
            }
            else if (p.Phase == ScanPhase.Inspecting)
            {
                ScanPhaseLabel.Text = "Inspecting";
                StatusLabel.Text = p.CurrentApp;
            }

            if (p.Total > 0)
            {
                ScanCountLabel.Text = $"{processed}/{p.Total}";
                ProgressBar.Progress = (double)processed / p.Total;
            }
        });
    }

    // ── App detected ──────────────────────────────────────────────────────────

    private void OnAppDetected(AppItem item)
    {
        // Only surface apps that SnapStak can actually open.
        // An app with no remote URL and no local asset bundle is a confirmed WebView
        // wrapper but its URL could not be extracted — it cannot be deconstructed.
        // Saving or displaying it would only confuse the user.
        bool hasUsableUrl = !string.IsNullOrEmpty(item.ExtractedUrl) ||
                            !string.IsNullOrEmpty(item.LocalIndexPath);
        if (!hasUsableUrl) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _detectedApps.Add(item);
            AppCollection.ItemsSource = null;
            AppCollection.ItemsSource = _detectedApps;
            AppCollection.IsVisible = true;

            DetectedBadge.IsVisible = true;
            DetectedBadgeLabel.Text = $"{_detectedApps.Count} found";
            ResultsCountLabel.Text = $"{_detectedApps.Count} app{(_detectedApps.Count == 1 ? "" : "s")}";

            await SaveAppAsync(item);
        });
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = e.NewTextValue?.ToLowerInvariant() ?? string.Empty;
        AppCollection.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _detectedApps
            : _detectedApps.Where(a =>
                (a.Name?.ToLowerInvariant().Contains(query) ?? false) ||
                (a.PackageName?.ToLowerInvariant().Contains(query) ?? false)).ToList();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private async void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not AppItem selected) return;
        ((CollectionView)sender).SelectedItem = null;

        string? url = await ResolveUrlAsync(selected);
        if (url == null) return;

        await Navigation.PushAsync(
            await DeconstructPage.CreateAsync(url, selected.Name ?? "App", selected.FrameworkBadge));
    }

    // ── URL resolution ────────────────────────────────────────────────────────

    private async Task<string?> ResolveUrlAsync(AppItem item)
    {
        if (!string.IsNullOrEmpty(item.ExtractedUrl)) return item.ExtractedUrl;

        if (item.IsLocalAsset &&
            !string.IsNullOrEmpty(item.ApkPath) &&
            !string.IsNullOrEmpty(item.PackageName) &&
            !string.IsNullOrEmpty(item.LocalIndexPath))
        {
            try
            {
                ScanPhaseLabel.Text = $"Preparing {item.Name}...";
                string fileUrl = await _extractor.GetLocalIndexUrlAsync(
                    item.PackageName, item.ApkPath, item.LocalIndexPath);
                ScanPhaseLabel.Text = "Scan complete";
                return fileUrl;
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Could not extract app assets:\n{ex.Message}", "OK");
                return null;
            }
        }

        return null;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private async Task SaveAppAsync(AppItem item)
    {
        try
        {
            string? iconBase64 = null;
            if (item.Icon is StreamImageSource src)
            {
                var stream = await src.Stream(CancellationToken.None);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    iconBase64 = Convert.ToBase64String(ms.ToArray());
                }
            }

            await _db.SaveAsync(new SavedApp
            {
                Name = item.Name ?? "",
                PackageName = item.PackageName ?? "",
                Url = item.ExtractedUrl ?? (item.IsLocalAsset ? $"local:{item.PackageName}" : ""),
                Framework = item.FrameworkBadge,
                Color = item.FrameworkBadgeColor,
                IconBase64 = iconBase64,
                ApkLastModified = item.ApkLastModified,
                ApkPath = item.ApkPath,
                LocalIndexPath = item.IsLocalAsset ? item.LocalIndexPath : null,
            });
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

}
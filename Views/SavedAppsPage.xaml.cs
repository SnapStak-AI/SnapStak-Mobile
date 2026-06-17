using SnapStakMobile.Models;
using SnapStakMobile.Services;

namespace SnapStakMobile.Views;

public partial class SavedAppsPage : ContentPage
{
    private readonly AppDatabaseService _db;
    private readonly AssetExtractorService _extractor = new();
    private List<SavedApp> _apps = new();

    public SavedAppsPage(AppDatabaseService db)
    {
        InitializeComponent();
        _db = db;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAppsAsync();
    }

    private async Task LoadAppsAsync()
    {
        _apps = await _db.GetAllAsync();

        if (_apps.Count == 0)
        {
            EmptyState.IsVisible = true;
            AppCollection.IsVisible = false;
            StatusLabel.Text = "No saved apps yet.";
        }
        else
        {
            EmptyState.IsVisible = false;
            AppCollection.IsVisible = true;
            AppCollection.ItemsSource = null;
            AppCollection.ItemsSource = _apps;
            StatusLabel.Text = $"{_apps.Count} saved app{(_apps.Count == 1 ? "" : "s")}";
        }
    }

    private async void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SavedApp selected)
            return;

        ((CollectionView)sender).SelectedItem = null;

        string? url = await ResolveUrlAsync(selected);
        if (url == null) return;

        await Navigation.PushAsync(
            await DeconstructPage.CreateAsync(url, selected.Name, selected.Framework));
    }

    // ── URL resolution ────────────────────────────────────────────────────────

    private async Task<string?> ResolveUrlAsync(SavedApp app)
    {
        // Remote URL apps — use the stored URL directly
        if (!app.IsLocalAsset)
            return app.Url;

        // Local asset apps — extract from APK and return a file:// path
        if (!string.IsNullOrEmpty(app.ApkPath) &&
            !string.IsNullOrEmpty(app.PackageName) &&
            !string.IsNullOrEmpty(app.LocalIndexPath))
        {
            try
            {
                StatusLabel.Text = $"Preparing {app.Name}...";
                string fileUrl = await _extractor.GetLocalIndexUrlAsync(
                    app.PackageName, app.ApkPath, app.LocalIndexPath);
                StatusLabel.Text = $"{_apps.Count} saved app{(_apps.Count == 1 ? "" : "s")}";
                return fileUrl;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"{_apps.Count} saved app{(_apps.Count == 1 ? "" : "s")}";
                await DisplayAlert("Error", $"Could not extract app assets:\n{ex.Message}", "OK");
                return null;
            }
        }

        // No usable URL — this record should never have been saved (it predates the
        // usable-URL gate in OnAppDetected). Delete it silently and refresh the list.
        await _db.DeleteAsync(app);
        await LoadAppsAsync();
        return null;
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is SavedApp app)
        {
            bool confirm = await DisplayAlert(
                "Delete App",
                $"Remove {app.Name} from saved apps?",
                "Delete", "Cancel");

            if (!confirm) return;

            await _db.DeleteAsync(app);
            await LoadAppsAsync();
        }
    }
}
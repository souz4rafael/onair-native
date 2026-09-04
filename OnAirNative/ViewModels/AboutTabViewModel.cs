using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnAirNative.Services;

namespace OnAirNative.ViewModels;

public partial class AboutTabViewModel : ObservableObject
{
    public string AppName      => "onAIr";
    public string Version      => "2.0.0";
    public string AuthorName   => "Rafael Souza (Microsoft)";
    public string AuthorCredit => "GitHub Copilot (Claude Sonnet 4.6)";
    public string Authors      => $"{AuthorName} · {AuthorCredit}";
    public string LinkedInUrl  => "https://www.linkedin.com/in/souzarafael";
    public string SourceUrl    => "https://github.com/souz4rafael/onair-native";
    public string Description =>
        "Transparent always-on-top TP for Windows.\n" +
        "Uses WinUI 3, whisper.net, and NAudio for native performance.";

    private readonly UpdateService _updateService;
    private UpdateCheckResult? _lastCheck;

    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool   _isCheckingForUpdate;
    [ObservableProperty] private bool   _isUpdateAvailable;
    [ObservableProperty] private bool   _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _latestVersion = "";

    /// <summary>Raised right after the downloaded installer has been launched — the
    /// View should close the app now so the installer can overwrite its files.</summary>
    public event EventHandler? InstallerLaunched;

    public AboutTabViewModel(UpdateService updateService) => _updateService = updateService;

    [RelayCommand]
    public void OpenSourceRepo() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(SourceUrl));

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdate) return;
        IsCheckingForUpdate = true;
        UpdateStatusText    = "Checking for updates…";
        try
        {
            _lastCheck        = await _updateService.CheckForUpdateAsync(Version);
            LatestVersion     = _lastCheck.LatestVersion;
            IsUpdateAvailable = _lastCheck.UpdateAvailable;
            UpdateStatusText  = _lastCheck.UpdateAvailable
                ? $"Update available: v{_lastCheck.LatestVersion}"
                : $"You're up to date (v{Version})";
        }
        catch (Exception ex)
        {
            IsUpdateAvailable = false;
            UpdateStatusText  = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    [RelayCommand]
    public async Task DownloadAndInstallAsync()
    {
        if (_lastCheck is not { UpdateAvailable: true, DownloadUrl: not null, AssetName: not null } result)
            return;

        IsDownloadingUpdate = true;
        DownloadProgress    = 0;
        UpdateStatusText    = "Downloading update… 0%";
        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                UpdateStatusText = $"Downloading update… {(int)(p * 100)}%";
            });

            var installerPath = await _updateService.DownloadInstallerAsync(result.DownloadUrl, result.AssetName, progress);

            UpdateStatusText = "Launching installer…";
            UpdateService.LaunchInstaller(installerPath);
            InstallerLaunched?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }
}

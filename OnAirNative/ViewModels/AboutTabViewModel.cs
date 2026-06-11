using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnAirNative.ViewModels;

public partial class AboutTabViewModel : ObservableObject
{
    public string AppName    => "onAIr Native";
    public string Version    => "1.0.2";
    public string Authors    => "Rafael Souza (Microsoft) · GitHub Copilot (Claude Sonnet 4.6)";
    public string SourceUrl  => "https://github.com/souz4rafael/onair";
    public string BaseApp    => "Based on onAIr v1.3.0 (Electron)";
    public string Description =>
        "Transparent always-on-top teleprompter overlay for Windows.\n" +
        "Uses WinUI 3, whisper.net, and NAudio for native performance.";

    [RelayCommand]
    public void OpenSourceRepo() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(SourceUrl));
}

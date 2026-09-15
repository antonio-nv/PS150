using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PS150.VideoEtAudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? filePath = desktop.Args is { Length: > 0 } ? desktop.Args[0] : null;

            if (filePath != null && PS150.Core.MediaKind.IsVideo(filePath))
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                window.Opened += (s, e) => window.PlayFile(filePath);
            }
            else
            {
                var window = new AudioPlayerWindow();
                desktop.MainWindow = window;
                if (filePath != null)
                {
                    window.Opened += (s, e) => window.PlayFile(filePath);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }


}
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
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (desktop.Args is { Length: > 0 })
            {
                // Počká na Opened, ať je VideoView opravdu hotové, než přijde Play()
                window.Opened += (s, e) => window.PlayFile(desktop.Args[0]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

}
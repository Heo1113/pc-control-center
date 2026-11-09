using System.Windows;

namespace WindowsAiOperator.TestTarget;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        string? suffix = null;
        var injection = false;
        var exitAfter = 0;
        for (var i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--title-suffix" when i + 1 < e.Args.Length:
                    suffix = e.Args[++i];
                    break;
                case "--injection-text":
                    injection = true;
                    break;
                case "--exit-after" when i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out var seconds):
                    exitAfter = seconds;
                    i++;
                    break;
            }
        }

        var window = new MainWindow(suffix, injection);
        window.Show();
        if (exitAfter > 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfter) };
            timer.Tick += (_, _) => Shutdown();
            timer.Start();
        }
    }
}

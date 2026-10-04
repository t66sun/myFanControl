using System.Windows;
using myFanControl.Diagnostics;

namespace myFanControl;

public partial class App : Application
{
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        foreach (MainWindow window in Windows.OfType<MainWindow>().ToArray())
            window.BeginSessionEnding();
        base.OnSessionEnding(e);
        e.Cancel = false;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 3 && e.Args[0] == "--sleep-test" && e.Args[1] == "--output") {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            _ = SleepProductProbe.RunAsync(e.Args[2]);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0] == "--control-test" && e.Args[1] == "--output") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunControlProbeAndExitAsync(e.Args[2]);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--window-test" && e.Args[1] == "--output") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunWindowProbeAndExitAsync(e.Args[2]);
            return;
        }

        if (ProbeCommand.IsProbeRequested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunProbeAndExitAsync(e.Args);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private async Task RunProbeAndExitAsync(string[] args)
    {
        var result = await ProbeRunner.RunAsync(args).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() => Shutdown(result.ExitCode));
    }

    private async Task RunControlProbeAndExitAsync(string path)
    {
        int exitCode;
        try { exitCode = await ControlProductProbe.RunAsync(path); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); exitCode = 3; }
        Shutdown(exitCode);
    }

    private async Task RunWindowProbeAndExitAsync(string path)
    {
        int exitCode;
        try { exitCode = await ControlProductProbe.RunWindowAsync(path); }
        catch(Exception exception) { System.Diagnostics.Debug.WriteLine(exception); exitCode=3; }
        Shutdown(exitCode);
    }
}

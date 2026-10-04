using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace myFanControl;

public partial class MainWindow
{
    private Task? _exitTask;
    private bool _sessionEnding;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterMonitor || _sessionEnding) return;

        e.Cancel = true;
        if (!_closing) HideToTray();
    }

    internal Task RequestExitAsync(bool discardUnsavedChanges = false)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync<Task>(() => RequestExitAsync(discardUnsavedChanges)).Task.Unwrap();
        if (_exitTask is not null) return _exitTask;
        if (_sessionEnding || _closeAfterMonitor || _closing) return Task.CompletedTask;

        _closing = true;
        if (!discardUnsavedChanges)
        {
            if (!IsVisible) ShowFromTray();
            if (!ConfirmDiscardUnsavedChanges())
            {
                _closing = false;
                return Task.CompletedTask;
            }
        }

        IsEnabled = false;
        _exitTask = ExitNormallyAsync();
        return _exitTask;
    }

    private async Task ExitNormallyAsync()
    {
        // Queue this before cancelling the monitor so its finally block cannot consume
        // a failed restore before the explicit exit path can report it.
        Task controlStop = StopControlAsync();
        _monitorCancellation.Cancel();
        Exception? restoreFailure = null;
        try { await controlStop; }
        catch (Exception exception) { restoreFailure = exception; }
        if (_monitorTask is not null)
        {
            try { await _monitorTask; }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }

        if (_sessionEnding || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (restoreFailure is not null)
        {
            ShowFromTray();
            MessageBox.Show(this,
                "已请求恢复固件控制，但后端清理报告错误：\n" + restoreFailure.Message +
                "\n程序将退出，请检查风扇状态。",
                "恢复固件控制未确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DisposeTrayUi();
        _closeAfterMonitor = true;
        Close();
    }

    internal void BeginSessionEnding()
    {
        if (_sessionEnding || _closeAfterMonitor) return;
        _sessionEnding = true;
        _closing = true;
        // Windows is allowed to terminate the UI immediately. Start normal release
        // without waiting or asking about drafts; the host also watches parent exit.
        Task? controlStop = _exitTask is null ? StopControlAsync() : null;
        _monitorCancellation.Cancel();
        DisposeTrayUi();
        if (controlStop is not null) _exitTask = ObserveSessionCleanupAsync(controlStop);
    }

    private async Task ObserveSessionCleanupAsync(Task controlStop)
    {
        try { await controlStop.ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine(exception); }
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }
    }
}

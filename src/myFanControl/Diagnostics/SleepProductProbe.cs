using System.Text.Json;
using EcPmcConfigProbe;
using Microsoft.Win32;
using myFanControl.Monitoring;

namespace myFanControl.Diagnostics;

/// <summary>Records real Windows power events; never requests sleep or reboot.</summary>
internal static class SleepProductProbe
{
    internal static async Task RunAsync(string outputPath)
    {
        using (new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write)) { }
        var window = new MainWindow();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportGate = new object();
        string stage = "Preparing";
        DateTimeOffset? suspend = null, resume = null;
        FanOverrideBaselineResult? before = null, after = null;
        var errors = new List<string>();
        bool succeeded = false;
        void Save() {
            lock(reportGate) File.WriteAllText(outputPath, JsonSerializer.Serialize(new {
                CapturedUtc=DateTimeOffset.UtcNow, Stage=stage, Succeeded=succeeded,
                SuspendUtc=suspend, ResumeUtc=resume, BeforeSleep=before, AfterResume=after,
                ActualWindowsSleepResumeVerified=succeeded,
                NotificationSource="Registered window WM_POWERBROADCAST (Modern Standby/DAM opt-in)",
                NotificationsRegistered=window.SuspendResumeNotificationsRegistered, Errors=errors.ToArray()
            }, new JsonSerializerOptions { WriteIndented=true }));
        }
        async void PowerChanged(PowerModes mode) {
            if(mode==PowerModes.Suspend) {
                suspend=DateTimeOffset.UtcNow; stage="SuspendObserved"; Save();
            } else if(mode==PowerModes.Resume && suspend is not null) {
                resume=DateTimeOffset.UtcNow;
                try {
                    await Task.Delay(3000);
                    after=await ControlProductProbe.ReadWindowAsync();
                    succeeded=suspend is not null && resume>suspend &&
                        after.RawCandidateOverride0C==0 && after.RawCandidateOverride0D==0 &&
                        after.RawCandidateOverride0E==0 && after.RawCandidateOverride0F==0;
                    if(!succeeded) errors.Add("Paired Windows sleep/resume and zero overrides were not verified.");
                    stage=succeeded?"Verified":"Failed";
                } catch(Exception exception) { errors.Add(exception.ToString()); stage="Failed"; }
                Save(); completion.TrySetResult();
            }
        }
        void Closed(object? sender, EventArgs e) {
            if(!completion.Task.IsCompleted) {
                errors.Add("Explicit exit ended the sleep/resume observation before it completed.");
                stage="Aborted"; Save(); completion.TrySetResult();
            }
        }
        try {
            window.Show();
            if(!window.SuspendResumeNotificationsRegistered) throw new IOException("Native suspend/resume registration unavailable.");
            window.SuspendResumeObserved += PowerChanged;
            window.Closed += Closed;
            using var monitor=new SensorMonitor(MachineIdentityReader.Read());
            await Task.Run(monitor.Open);
            await window.ProcessControlSnapshotAsync(await Task.Run(monitor.Capture));
            await window.ApplyManualAsync(3600,3800);
            before=await ControlProductProbe.ReadWindowAsync();
            if(before.RawCandidateOverride0C!=36 || before.RawCandidateOverride0D!=38)
                throw new IOException("Expected manual overrides were not active before sleep.");
            stage="ReadyForManualSleep"; Save();
            window.Title = "ThinkBook 睡眠实测 · 手动睡眠约 20 秒后唤醒；X 入托盘，托盘退出可终止";
            await completion.Task;
            if (stage == "Aborted") return;
            // The observer does not issue a restore that could mask a product failure.
            window.ShowFromTray();
            System.Windows.MessageBox.Show(window, succeeded
                ? "睡眠与唤醒实测通过，两路覆盖已清零。关闭窗口会进入托盘；结束程序请在托盘选择“恢复固件并退出”。"
                : "睡眠实测未通过，详见诊断报告。请点击恢复固件控制。关闭窗口会进入托盘，不会终止程序。", "myFanControl 实测");
        } catch(Exception exception) {
            errors.Add(exception.ToString()); stage="Failed"; Save();
            try { await window.RestoreFirmwareAsync(); } catch(Exception restore) { errors.Add(restore.Message); Save(); }
        } finally {
            window.SuspendResumeObserved -= PowerChanged;
            window.Closed -= Closed;
        }
    }
}

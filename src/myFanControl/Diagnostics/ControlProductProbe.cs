using System.Diagnostics;
using System.Text.Json;
using EcIdentityProbe;
using EcFeedbackProbe;
using EcPmcConfigProbe;
using myFanControl.Monitoring;

namespace myFanControl.Diagnostics;

internal static class ControlProductProbe
{
    internal static async Task<int> RunWindowAsync(string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var errors = new List<string>();
        var window = new MainWindow();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        FanOverrideBaselineResult? beforeHide = null, afterHide = null, afterExit = null;
        bool closeHidden = false, shownFromTray = false, explicitlyExited = false;
        const int hiddenHeartbeatSeconds = 12;
        try {
            using var monitor = new SensorMonitor(MachineIdentityReader.Read());
            await Task.Run(monitor.Open);
            window.Show();
            await window.ProcessControlSnapshotAsync(await Task.Run(monitor.Capture));
            await window.ApplyManualAsync(3600, 3800);
            await Task.Delay(1500);
            beforeHide = await ReadWindowAsync();
            if(beforeHide.RawCandidateOverride0C != 36 || beforeHide.RawCandidateOverride0D != 38)
                throw new IOException("Window manual control was not active before hiding.");
            window.Close();
            closeHidden = !window.IsVisible && !closed.Task.IsCompleted;
            if (!closeHidden) throw new IOException("Window close did not hide to the tray while keeping the window alive.");
            // Exceed the host's ten-second heartbeat timeout while hidden. The product
            // monitor alone must keep control alive; this probe sends no heartbeats.
            await Task.Delay(TimeSpan.FromSeconds(hiddenHeartbeatSeconds));
            afterHide = await ReadWindowAsync();
            if (afterHide.RawCandidateOverride0C != 36 || afterHide.RawCandidateOverride0D != 38)
                throw new IOException("Manual control or its heartbeat stopped while hidden in the tray.");
            window.ShowFromTray();
            shownFromTray = window.IsVisible && window.WindowState == System.Windows.WindowState.Normal;
            if (!shownFromTray) throw new IOException("Tray restore did not show the window.");
            await window.RequestExitAsync(discardUnsavedChanges: true);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            explicitlyExited = true;
            afterExit = await ReadWindowAsync(); RequireZero(afterExit);
        } catch(Exception e) { errors.Add(e.ToString()); }
        finally {
            try { await window.RequestExitAsync(discardUnsavedChanges: true); } catch(Exception e) { errors.Add("Final exit: "+e.Message); }
        }
        JsonSerializer.Serialize(output, new { CapturedUtc=DateTimeOffset.UtcNow, Succeeded=errors.Count==0,
            BeforeHide=beforeHide, AfterHide=afterHide, AfterExit=afterExit,
            BeforeClose=beforeHide, AfterClose=afterExit,
            Evidence=new[] {
                new { Stage="BeforeHide", Window=beforeHide },
                new { Stage="AfterHide", Window=afterHide },
                new { Stage="AfterExit", Window=afterExit }
            },
            CloseHidden=closeHidden, ShownFromTray=shownFromTray, ExplicitlyExited=explicitlyExited,
            HiddenHeartbeatSeconds=hiddenHeartbeatSeconds, Errors=errors }, new JsonSerializerOptions { WriteIndented=true });
        return errors.Count==0?0:2;
    }

    internal static async Task<int> RunAsync(string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var evidence = new List<object>(); var errors = new List<string>();
        var window = new MainWindow();
        window.InitializeControlUi();
        try {
            using var monitor = new SensorMonitor(MachineIdentityReader.Read());
            await Task.Run(monitor.Open);
            var initial = await ReadWindowAsync(); RequireZero(initial);
            evidence.Add(new { Stage="Initial", Window=initial });
            await window.ProcessControlSnapshotAsync(await Task.Run(monitor.Capture));
            await window.ApplyManualAsync(3600, 3800);
            bool fan1Moved = false, fan2Moved = false;
            for (int i=0; i<10; i++) {
                await Task.Delay(1000);
                var temperature = await Task.Run(monitor.Capture);
                await window.ProcessControlSnapshotAsync(temperature);
                var raw = await ReadWindowAsync();
                evidence.Add(new { Stage="Manual", Temperature=temperature, Window=raw });
                if(raw.RawCandidateOverride0C != 36 || raw.RawCandidateOverride0D != 38) throw new IOException("Manual override was not applied.");
                fan1Moved |= Math.Abs(raw.Fan1Rpm!.Value-3600)<=150;
                fan2Moved |= Math.Abs(raw.Fan2Rpm!.Value-3800)<=150;
            }
            if(!fan1Moved || !fan2Moved) throw new IOException("Manual target feedback missing.");
            await window.ApplyManualAsync(4200, 4400);
            fan1Moved = false; fan2Moved = false;
            for (int i=0; i<10; i++) {
                await Task.Delay(1000);
                await window.ProcessControlSnapshotAsync(await Task.Run(monitor.Capture));
                var raw = await ReadWindowAsync();
                evidence.Add(new { Stage="ManualStep", Window=raw });
                if(raw.RawCandidateOverride0C != 42 || raw.RawCandidateOverride0D != 44)
                    throw new IOException("Second manual targets were not applied.");
                fan1Moved |= Math.Abs(raw.Fan1Rpm!.Value-4200)<=150;
                fan2Moved |= Math.Abs(raw.Fan2Rpm!.Value-4400)<=150;
            }
            if(!fan1Moved || !fan2Moved) throw new IOException("Manual feedback did not follow the second targets.");
            await window.RestoreFirmwareAsync();
            var restored = await ReadWindowAsync(); RequireZero(restored);
            evidence.Add(new { Stage="ManualRestore", Window=restored });
            await window.ProcessControlSnapshotAsync(await Task.Run(monitor.Capture));
            await window.ApplyCurvesAsync("40:2500;60:3500;75:5000;90:7500", "40:2500;60:3500;75:5000;90:7500");
            bool curveMoved=false;
            for(int i=0;i<12;i++) {
                await Task.Delay(1000);
                var temperature=await Task.Run(monitor.Capture);
                await window.ProcessControlSnapshotAsync(temperature);
                var raw=await ReadWindowAsync();
                evidence.Add(new { Stage="Curve", Temperature=temperature, Window=raw });
                if(raw.RawCandidateOverride0C is not (>=15 and <=75) || raw.RawCandidateOverride0D is not (>=15 and <=75))
                    throw new IOException("Curve execution did not apply two RPM overrides.");
                curveMoved |= Math.Abs(raw.Fan1Rpm!.Value-raw.RawCandidateOverride0C!.Value*100)<=150 &&
                    Math.Abs(raw.Fan2Rpm!.Value-raw.RawCandidateOverride0D!.Value*100)<=150;
            }
            if(!curveMoved) throw new IOException("Curve feedback movement missing.");
            await window.ProcessControlSnapshotAsync(new TemperatureSnapshot(DateTimeOffset.UtcNow, [], ["Injected missing CPU temperature"]));
            restored=await ReadWindowAsync(); RequireZero(restored);
            evidence.Add(new { Stage="MissingTemperatureRestore", Window=restored });
            await window.StopControlAsync();

            // Closing stdin without an explicit firmware command models loss of the UI channel.
            var eofClient=new ControlClient();
            await eofClient.SetAsync(3600,3800);
            await eofClient.DisposeAsync();
            restored=await ReadWindowAsync(); RequireZero(restored);
            evidence.Add(new { Stage="InputChannelClosedRestore", Window=restored });

            var timeoutClient=new ControlClient();
            await timeoutClient.SetAsync(3600,3800);
            await Task.Delay(TimeSpan.FromSeconds(12));
            restored=await ReadWindowAsync(); RequireZero(restored);
            string? timeoutExit=null;
            try { await timeoutClient.DisposeAsync(); } catch(IOException e) { timeoutExit=e.Message; }
            if(timeoutExit is null || !timeoutExit.Contains("heartbeat",StringComparison.OrdinalIgnoreCase))
                throw new IOException("Missing expected host heartbeat timeout.");
            evidence.Add(new { Stage="HeartbeatTimeoutRestore", Window=restored, HostExit=timeoutExit });

            using var stub=new Process { StartInfo=new ProcessStartInfo("powershell.exe") {
                UseShellExecute=false,CreateNoWindow=true } };
            stub.StartInfo.ArgumentList.Add("-NoProfile"); stub.StartInfo.ArgumentList.Add("-Command"); stub.StartInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
            stub.Start();
            var parentClient=new ControlClient(stub.Id);
            try {
                await parentClient.SetAsync(3600,3800);
                stub.Kill(); await stub.WaitForExitAsync(); await Task.Delay(1500);
                restored=await ReadWindowAsync(); RequireZero(restored);
                string? parentExit=null;
                try { await parentClient.DisposeAsync(); } catch(IOException e) { parentExit=e.Message; }
                if(parentExit is null || !parentExit.Contains("Parent",StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected parent exit diagnostic missing.");
                evidence.Add(new { Stage="ParentExitedRestore", Window=restored, HostExit=parentExit });
            } finally {
                if(!stub.HasExited) stub.Kill();
                await parentClient.DisposeAsync();
            }
        } catch(Exception e) { errors.Add(e.ToString()); }
        finally {
            try { await window.StopControlAsync(); } catch(Exception e) { errors.Add("Final restore: "+e.Message); }
        }
        JsonSerializer.Serialize(output,new { CapturedUtc=DateTimeOffset.UtcNow,Succeeded=errors.Count==0,
            Evidence=evidence,Errors=errors,ActualWindowsSleepResumeVerified=false,NormalBootVerified=false },new JsonSerializerOptions {WriteIndented=true});
        return errors.Count==0?0:2;
    }
    internal static async Task<FanOverrideBaselineResult> ReadWindowAsync()
    {
        return await Task.Run(() => {
            var wait=new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if(wait.Status!=EcMutexWaitStatus.Acquired || wait.Lease is null) throw new IOException("ISA mutex unavailable.");
            PawnSelectorIo io;
            using(wait.Lease) io=new PawnSelectorIo();
            using(io) {
                var reader=new FanOverrideBaselineReader(io);
                for(int i=0;i<8;i++) {
                    var x=reader.Read();
                    if(x.Available) return x;
                    if(!x.SelectorsRestored || x.ChipIdBefore!=0x5571 || x.ChipIdAfter!=0x5571 || x.Mapping1060Before!=0 || x.Mapping1060After!=0 ||
                        !(x.Errors.Count==1 && x.FirstBytes is {Length:16} a && x.SecondBytes is {Length:16} b && !a.SequenceEqual(b)))
                        throw new IOException("Window read failed: "+string.Join("; ",x.Errors));
                    Thread.Sleep(100);
                }
                throw new IOException("No coherent window snapshot within eight bounded reads.");
            }
        });
    }
    private static void RequireZero(FanOverrideBaselineResult x) {
        if(x.RawCandidateOverride0C!=0 || x.RawCandidateOverride0D!=0 || x.RawCandidateOverride0E!=0 || x.RawCandidateOverride0F!=0)
            throw new IOException("Firmware automatic restore not observed.");
    }
}

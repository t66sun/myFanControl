using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using FanControl.Core;
using EcFeedbackProbe;
namespace EcPmcConfigProbe;

internal static class RpmTrialProbe
{
    internal static int Run(string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var errors = new List<string>(); var trials = new List<object>();
        FanOverrideBaselineResult? initial = null;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (bios?.GetValue("SystemProductName") as string != "21CX" || bios.GetValue("BIOSVersion") as string != "HYCN42WW") return 5;
        using var timer = new PollTimerResolution();
        try {
            PnpSelectorIo io;
            var wait = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if (wait.Lease is null || wait.Status != EcMutexWaitStatus.Acquired) throw new IOException("ISA mutex unavailable.");
            using (wait.Lease) io = new PnpSelectorIo();
            using (io) {
                var baselineReader = new FanOverrideBaselineReader(io);
                initial = baselineReader.Read(); RequireAutomatic(initial);
                var transport = new DeveloperQueryTransport(io, true);
                var reader = new Pmc2QueryReader(transport);
                var initialQuery = reader.Read();
                if (initialQuery.Flags != 0) throw new IOException("Initial flags differ from validated zero baseline.");
                for (int fan = 1; fan <= 2; fan++) {
                    var baseline = baselineReader.Read(); RequireAutomatic(baseline);
                    byte originalTarget = (fan == 1 ? baseline.Target0804 : baseline.Target0805)!.Value;
                    if (originalTarget is < 15 or > 73) throw new IOException("Baseline target cannot support +200 RPM trial.");
                    byte target = (byte)(originalTarget + 2);
                    bool attempted = false, restored = false, aliasObserved = false;
                    var observations = new List<object>(); var restoration = new List<object>();
                    string? failure = null; bool rpmMoved = false;
                    try {
                        attempted = true; transport.SetRpmOverride(fan, target);
                        for (int sample = 0; sample < 16; sample++) {
                            Thread.Sleep(500);
                            var q = reader.Read(); var raw = baselineReader.Read();
                            observations.Add(new { Query = q, Window = raw });
                            if (!raw.SelectorsRestored || q.Flags != initialQuery.Flags) throw new IOException("Identity/restoration or flags changed.");
                            if (raw.Available) {
                                if (raw.RawCandidateOverride0E != 0 || raw.RawCandidateOverride0F != 0 ||
                                    (fan == 1 ? raw.RawCandidateOverride0D : raw.RawCandidateOverride0C) != 0)
                                    throw new IOException("Unowned override changed.");
                                aliasObserved |= (fan == 1 ? raw.RawCandidateOverride0C : raw.RawCandidateOverride0D) == target;
                            }
                            int rpm = fan == 1 ? q.Fan1Rpm : q.Fan2Rpm;
                            int oldRpm = fan == 1 ? initialQuery.Fan1Rpm : initialQuery.Fan2Rpm;
                            rpmMoved |= rpm >= oldRpm + 100 && Math.Abs(rpm-target*100) <= 150;
                        }
                    } catch (Exception e) { failure = e.Message; }
                    finally {
                        if (attempted) {
                            try {
                                transport.SetRpmOverride(fan, 0);
                                for (int i = 0; i < 6; i++) {
                                    Thread.Sleep(500);
                                    var raw = baselineReader.Read();
                                    restoration.Add(raw);
                                    if (raw.Available) { RequireAutomatic(raw); restored = true; }
                                }
                                // A poisoned reader is never retried after a partial query.
                                if (!reader.Stopped && reader.Read().Flags != initialQuery.Flags) restored = false;
                            } catch (Exception e) { failure = (failure is null ? "" : failure + "; ") + "Restore: " + e.Message; restored = false; }
                        }
                        trials.Add(new { Fan = fan, TargetRpm = target*100, OriginalTargetRpm = originalTarget*100,
                            Attempted = attempted, AliasObserved = aliasObserved, FeedbackMovedTowardTarget = rpmMoved,
                            Restored = restored, Observations = observations, Restoration = restoration, Error = failure,
                            LastTransportTrace = transport.Trace.ToArray() });
                    }
                    if (failure is not null || !restored || !aliasObserved || !rpmMoved)
                        throw new IOException($"Fan {fan} trial incomplete: restore={restored}, alias={aliasObserved}, rpmMovement={rpmMoved}; {failure}");
                }
            }
        } catch (Exception e) { errors.Add(e.Message); }
        bool passed = trials.Count == 2 && errors.Count == 0;
        JsonSerializer.Serialize(output, new { CapturedUtc = DateTimeOffset.UtcNow, Succeeded = passed,
            ModuleSha256 = DeveloperQueryTransport.ControlModuleHash, Initial = initial, Trials = trials, Errors = errors,
            IndependentPhysicalTachometerVerified = false }, new JsonSerializerOptions { WriteIndented = true });
        return passed ? 0 : 2;
    }
    private static void RequireAutomatic(FanOverrideBaselineResult x) {
        if (!x.Available || !x.SelectorsRestored || x.RawCandidateOverride0C != 0 || x.RawCandidateOverride0D != 0 ||
            x.RawCandidateOverride0E != 0 || x.RawCandidateOverride0F != 0)
            throw new IOException("Coherent zero-override baseline required: " + string.Join("; ", x.Errors));
    }
}

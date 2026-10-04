using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using EcFeedbackProbe;
namespace EcPmcConfigProbe;

internal static class SignedRpmTrialProbe
{
    internal static int Run(string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var trials = new List<object>(); var errors = new List<string>();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (bios?.GetValue("SystemProductName") as string != "21CX" || bios.GetValue("BIOSVersion") as string != "HYCN42WW") return 5;
        try {
            PnpSelectorIo io;
            var wait = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if (wait.Lease is null || wait.Status != EcMutexWaitStatus.Acquired) throw new IOException("ISA mutex unavailable.");
            using (wait.Lease) io = new PnpSelectorIo();
            using (io) {
                var reader = new FanOverrideBaselineReader(io);
                var writer = new FixedRpmOverrideWriter(io);
                for (int fan = 1; fan <= 2; fan++) {
                    var before = reader.Read(); RequireZero(before);
                    byte oldTarget = (fan == 1 ? before.Target0804 : before.Target0805)!.Value;
                    int beforeRpm = (fan == 1 ? before.Fan1Rpm : before.Fan2Rpm)!.Value;
                    if (oldTarget is < 15 or > 73) throw new IOException("Baseline cannot support +200 RPM.");
                    byte target = (byte)(oldTarget+2);
                    bool attempted=false, alias=false, moved=false, restored=false;
                    var samples = new List<FanOverrideBaselineResult>(); var restoration = new List<FanOverrideBaselineResult>();
                    string? failure=null;
                    try {
                        attempted=true; writer.Set(fan,target);
                        for (int i=0; i<16; i++) {
                            Thread.Sleep(500); var x=reader.Read(); samples.Add(x); RequireSafe(x);
                            if (!x.Available) continue;
                            if (x.RawCandidateOverride0E!=0 || x.RawCandidateOverride0F!=0 ||
                                (fan==1 ? x.RawCandidateOverride0D : x.RawCandidateOverride0C)!=0) throw new IOException("Unowned override changed.");
                            alias |= (fan==1 ? x.RawCandidateOverride0C : x.RawCandidateOverride0D)==target;
                            int rpm=(fan==1 ? x.Fan1Rpm : x.Fan2Rpm)!.Value;
                            moved |= rpm>=beforeRpm+100 && Math.Abs(rpm-target*100)<=150;
                        }
                    } catch(Exception e) { failure=e.Message; }
                    finally {
                        if(attempted) try {
                            writer.Set(fan,0);
                            for(int i=0;i<8;i++) {
                                Thread.Sleep(500); var x=reader.Read(); restoration.Add(x); RequireSafe(x);
                                if (!x.Available) continue;
                                RequireZero(x);
                                int rpm=(fan==1 ? x.Fan1Rpm : x.Fan2Rpm)!.Value;
                                byte currentTarget=(fan==1 ? x.Target0804 : x.Target0805)!.Value;
                                restored |= currentTarget==oldTarget && rpm<=beforeRpm+100;
                            }
                        } catch(Exception e) { restored=false; failure=(failure??"")+" Restore: "+e.Message; }
                        trials.Add(new { Fan=fan, TargetRpm=target*100, Before=before, OverrideObserved=alias,
                            FeedbackMovedTowardTarget=moved, Restored=restored, Samples=samples, Restoration=restoration, Error=failure });
                    }
                    if(failure!=null || !alias || !moved || !restored) throw new IOException($"Fan {fan} trial incomplete: alias={alias}, movement={moved}, restore={restored}; {failure}");
                }
            }
        } catch(Exception e) { errors.Add(e.Message); }
        bool succeeded=trials.Count==2 && errors.Count==0;
        JsonSerializer.Serialize(output,new { CapturedUtc=DateTimeOffset.UtcNow, Succeeded=succeeded,
            Backend="Official signed LpcIO; fixed indirect 080C/080D RPM writes; no PMC or driver switch",
            SignedModuleSha256=PnpSelectorIo.ExpectedModuleHash, Trials=trials, Errors=errors,
            NormalBootVerified=false, IndependentPhysicalTachometerVerified=false },new JsonSerializerOptions {WriteIndented=true});
        return succeeded?0:2;
    }
    private static void RequireSafe(FanOverrideBaselineResult x) {
        if(!x.SelectorsRestored || x.ChipIdBefore!=0x5571 || x.ChipIdAfter!=0x5571 ||
            x.Mapping1060Before!=0 || x.Mapping1060After!=0 || x.StopwatchElapsedMilliseconds is not (>=0 and <=2000))
            throw new IOException("Window identity/selector/timing validation failed.");
        if(!x.Available && !(x.Errors.Count==1 && x.FirstBytes is {Length:16} a && x.SecondBytes is {Length:16} b && !a.SequenceEqual(b)))
            throw new IOException("Invalid snapshot: "+string.Join("; ",x.Errors));
    }
    private static void RequireZero(FanOverrideBaselineResult x) {
        RequireSafe(x);
        if(!x.Available || x.RawCandidateOverride0C!=0 || x.RawCandidateOverride0D!=0 || x.RawCandidateOverride0E!=0 || x.RawCandidateOverride0F!=0)
            throw new IOException("Coherent zero override baseline required.");
    }
}

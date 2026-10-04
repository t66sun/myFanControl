using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using FanControl.Core;
using EcFeedbackProbe;

namespace EcPmcConfigProbe;

internal static class DeveloperQueryProbe
{
    internal static int Run(string outputPath, bool recoverPreviousResponse = false)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var errors = new List<string>();
        var samples = new List<object>();
        var referenceWarnings = new List<string>();
        int crossCheckedSamples = 0;
        object? failedTransaction = null;
        PollTimerResolution? timer = null;
        QueryResponseRecoveryResult? recovery = null;
        FanOverrideBaselineResult? overrideBaseline = null;
        bool stopped = false;
        try {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
            if (bios?.GetValue("SystemProductName") as string != "21CX" || bios.GetValue("BIOSVersion") as string != "HYCN42WW")
                throw new NotSupportedException("Machine/BIOS mismatch; no device opened.");
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Administrator required; no device opened.");
            timer = new PollTimerResolution();
            // Creating the signed selector client selects only slot 1; selector
            // reads/writes below happen with their own or transport ISA lease.
            var initialWait = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if (initialWait.Status != EcMutexWaitStatus.Acquired || initialWait.Lease is null)
                throw new InvalidOperationException("ISA mutex unavailable.");
            PnpSelectorIo io;
            using (initialWait.Lease) io = new PnpSelectorIo();
            using (io) {
                var ecReader = new LowFeedbackReader(io);
                var transport = new DeveloperQueryTransport(io);
                if (recoverPreviousResponse) {
                    try { recovery = QueryResponseRecovery.Run(transport); }
                    catch (Exception e) {
                        recovery = new((byte[]?)e.Data["RecoveryDiscardedBytes"] ?? [],
                            e.Data["RecoveryElapsedMilliseconds"] is double elapsed ? elapsed : 0, false);
                        failedTransaction = new { transport.BeforeIdentity, transport.AfterIdentity, transport.Configuration, Trace = transport.Trace.ToArray() };
                        throw;
                    }
                }
                overrideBaseline = new FanOverrideBaselineReader(io).Read();
                if (!overrideBaseline.SelectorsRestored || overrideBaseline.ChipIdBefore != 0x5571 ||
                    overrideBaseline.ChipIdAfter != 0x5571 || overrideBaseline.Mapping1060Before != 0 ||
                    overrideBaseline.Mapping1060After != 0 || overrideBaseline.StopwatchElapsedMilliseconds is not (>= 0 and <= 2000))
                    throw new InvalidDataException("Override baseline identity/selector/timing check failed; no further queries.");
                var reader = new Pmc2QueryReader(transport);
                try {
                    for (int i = 0; i < 3; i++) {
                        var before = ecReader.Read();
                        RequireSafeReference(before);
                        Pmc2FanFeedback query;
                        try { query = reader.Read(); }
                        catch {
                            failedTransaction = new { transport.BeforeIdentity, transport.AfterIdentity, transport.Configuration, Trace = transport.Trace.ToArray() };
                            throw;
                        }
                        var after = ecReader.Read();
                        bool? matches = before.Available || after.Available
                            ? Near(query.Fan1Rpm, before.Fan1Rpm, after.Fan1Rpm) && Near(query.Fan2Rpm, before.Fan2Rpm, after.Fan2Rpm) : null;
                        samples.Add(new { Before = before, Query = query, After = after, CrossCheckWithin300Rpm = matches,
                            transport.BeforeIdentity, transport.AfterIdentity, transport.Configuration, Trace = transport.Trace.ToArray() });
                        RequireSafeReference(after);
                        if (!before.Available || !after.Available) referenceWarnings.Add($"Sample {i+1}: changing EC snapshot rejected; comparison uses only available coherent snapshots.");
                        if (matches == false) throw new InvalidDataException("PMC2 versus EC feedback differs by more than 300 RPM; no more queries.");
                        if (matches == true) crossCheckedSamples++;
                        if (i < 2) Thread.Sleep(500);
                    }
                } finally { stopped = reader.Stopped; }
            }
        } catch (Exception e) { errors.Add($"{e.GetType().Name}: {e.Message}"); }
        finally {
            try { timer?.Dispose(); }
            catch (Exception e) { errors.Add($"Timer release failed: {e.Message}"); }
        }
        bool succeeded = samples.Count == 3 && crossCheckedSamples > 0 && errors.Count == 0;
        JsonSerializer.Serialize(output, new { SchemaVersion = 1, CapturedUtc = DateTimeOffset.UtcNow,
            Mode = "Fixed D5/12,18,19 query only; no fan settings", ModuleSha256 = DeveloperQueryTransport.ExpectedModuleHash,
            Succeeded = succeeded, QueryReaderStopped = stopped, Samples = samples, FailedTransaction = failedTransaction, CrossCheckedSamples = crossCheckedSamples, ReferenceWarnings = referenceWarnings, Errors = errors,
            TimerResolutionRequestMs = timer is null ? (int?)null : 1, TimerEndResult = timer?.EndResult,
            PreviousQueryResponseRecovery = recovery,
            OverrideBaseline = overrideBaseline,
            FanTargetCommandsSent = false, PhysicalRpmVerified = false, ControlVerified = false }, new JsonSerializerOptions { WriteIndented = true });
        return succeeded ? 0 : 2;
    }
    private static bool Near(int query, int? before, int? after)
    {
        if (before is int b && after is int a) return query >= Math.Min(b,a)-300 && query <= Math.Max(b,a)+300;
        return before is int x ? Math.Abs(query-x) <= 300 : after is int y && Math.Abs(query-y) <= 300;
    }
    private static void RequireSafeReference(LowFeedbackReadResult reference)
    {
        if (reference.Available) return;
        // A changing, non-atomic RPM window is a missing comparison sample,
        // not a reason to retry a PMC transaction. Preserve both raw snapshots.
        if (reference.ReadCompleted && reference.SelectorsRestored && !reference.ReaderStopped &&
            reference.ChipIdBefore == 0x5571 && reference.ChipIdAfter == 0x5571 &&
            reference.Mapping1060Before == 0 && reference.Mapping1060After == 0 &&
            reference.StopwatchElapsedMilliseconds is >= 0 and <= 2000 &&
            reference.Errors.Count == 1 && reference.FirstBytes is { Length: 6 } first &&
            reference.SecondBytes is { Length: 6 } second && !first.SequenceEqual(second)) return;
        throw new InvalidDataException("EC reference hardware/restoration check failed: " + string.Join("; ", reference.Errors));
    }
}

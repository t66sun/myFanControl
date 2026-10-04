using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using EcFeedbackProbe;
using Microsoft.Win32;

namespace EcPmcConfigProbe;

/// <summary>Owns the signed EC writer for one parent application's control session.</summary>
internal static class ControlHost
{
    private static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(10);

    internal static int Run(int parentPid)
    {
        PnpSelectorIo? io = null;
        var owned = false;
        int exitCode = 2;
        bool ready = false;
        using var ownerMutex = new Mutex(false, @"Global\ThinkBookControl.21CX.RpmOwner");
        bool ownerAcquired = false;
        try
        {
            if (parentPid <= 0 || parentPid == Environment.ProcessId)
                throw new ArgumentOutOfRangeException(nameof(parentPid), "A distinct live parent process ID is required.");
            using var parent = Process.GetProcessById(parentPid);
            DateTime parentStart = parent.StartTime.ToUniversalTime();
            RequireParent(parent, parentStart);
            RequireMachineAndAdministrator();
            try { ownerAcquired = ownerMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownerMutex.ReleaseMutex(); throw new IOException("Previous control host exited unexpectedly; verify overrides before starting another session."); }
            if (!ownerAcquired) throw new IOException("Another control session is already running.");

            var initialLease = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if (initialLease.Status != EcMutexWaitStatus.Acquired || initialLease.Lease is null)
                throw new IOException("ISA mutex unavailable during control host initialization.");
            using (initialLease.Lease) io = new PnpSelectorIo();
            var baselineReader = new FanOverrideBaselineReader(io);
            ReadAutomaticBaseline(baselineReader.Read, () => Thread.Sleep(100));
            RequireParent(parent, parentStart);

            var writer = new FixedRpmOverrideWriter(io);
            DateTimeOffset lastHeartbeat = DateTimeOffset.UtcNow;
            ready = true;
            WriteAck(true, null);
            exitCode = 0;

            Task<string?> pendingLine = Task.Run(() => Console.In.ReadLine());
            while (true)
            {
                RequireParent(parent, parentStart);
                if (DateTimeOffset.UtcNow - lastHeartbeat >= HeartbeatLimit)
                    throw new TimeoutException("Parent heartbeat was not received within 10 seconds.");

                if (!pendingLine.IsCompleted)
                {
                    Thread.Sleep(200);
                    continue;
                }

                string? line;
                try { line = pendingLine.GetAwaiter().GetResult(); }
                catch (Exception e) { throw new IOException("Control command input failed.", e); }
                if (line is null) break; // Parent closed stdin: restore owned targets below.

                try
                {
                    using JsonDocument command = JsonDocument.Parse(line);
                    JsonElement root = command.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mode", out JsonElement modeElement) || modeElement.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Command requires a string mode.");
                    string mode = modeElement.GetString()!;
                    switch (mode)
                    {
                        case "heartbeat":
                            lastHeartbeat = DateTimeOffset.UtcNow;
                            break;
                        case "manual":
                        {
                            int fan1 = ReadRpm(root, "fan1");
                            int fan2 = ReadRpm(root, "fan2");
                            // From this point on, any partial failure must trigger both zero writes.
                            owned = true;
                            writer.Set(1, checked((byte)(fan1 / 100)));
                            writer.Set(2, checked((byte)(fan2 / 100)));
                            break;
                        }
                        case "firmware":
                            ClearBoth(writer);
                            owned = false;
                            break;
                        default:
                            throw new InvalidDataException("Mode must be manual, firmware, or heartbeat.");
                    }
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    WriteAck(true, null);
                }
                catch (Exception e)
                {
                    WriteAck(false, e.Message);
                    Console.Error.WriteLine("Control command failed: " + e);
                    exitCode = 2;
                    break;
                }
                pendingLine = Task.Run(() => Console.In.ReadLine());
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Control host failed: " + e);
            if (!ready) WriteAck(false, e.Message);
            exitCode = 2;
        }
        finally
        {
            if (owned && io is not null)
            {
                try { ClearBoth(new FixedRpmOverrideWriter(io)); }
                catch (Exception e)
                {
                    Console.Error.WriteLine("Final override clear failed: " + e);
                    exitCode = 2;
                }
            }
            io?.Dispose();
            if (ownerAcquired) ownerMutex.ReleaseMutex();
        }
        return exitCode;
    }

    private static int ReadRpm(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) || !element.TryGetInt32(out int rpm) ||
            rpm is < 1500 or > 7500 || rpm % 100 != 0)
            throw new InvalidDataException($"{name} must be an integer RPM value from 1500 through 7500 in 100 RPM steps.");
        return rpm;
    }

    private static void ClearBoth(FixedRpmOverrideWriter writer)
    {
        Exception? first = null;
        try { writer.Set(1, 0); } catch (Exception e) { first = e; }
        try { writer.Set(2, 0); } catch (Exception e) { first ??= e; }
        if (first is not null) throw new IOException("Could not verify zero overrides for both fans.", first);
    }

    private static void RequireMachineAndAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator token required.");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
        if (bios?.GetValue("SystemProductName") as string != "21CX" ||
            bios.GetValue("BIOSVersion") as string != "HYCN42WW")
            throw new NotSupportedException("Machine/BIOS profile mismatch.");
    }

    private static void RequireAutomaticBaseline(FanOverrideBaselineResult baseline)
    {
        if (!baseline.Available || !baseline.SelectorsRestored ||
            baseline.RawCandidateOverride0C != 0 || baseline.RawCandidateOverride0D != 0 ||
            baseline.RawCandidateOverride0E != 0 || baseline.RawCandidateOverride0F != 0)
            throw new IOException("A coherent zero-override baseline is required before control: " + string.Join("; ", baseline.Errors));
    }

    internal static FanOverrideBaselineResult ReadAutomaticBaseline(Func<FanOverrideBaselineResult> read, Action delay)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var baseline = read();
            if (baseline.Available)
            {
                RequireAutomaticBaseline(baseline);
                return baseline;
            }
            // RPM feedback can update between snapshots. Retry only that read-only
            // race, with intact identity/restoration and zero overrides in both.
            if (!baseline.SelectorsRestored || baseline.ChipIdBefore != 0x5571 ||
                baseline.ChipIdAfter != 0x5571 || baseline.Mapping1060Before != 0 ||
                baseline.Mapping1060After != 0 || baseline.Errors.Count != 1 ||
                baseline.Errors[0] != "The two complete 0800-080F snapshots differ." ||
                baseline.FirstBytes is not { Length: 16 } first ||
                baseline.SecondBytes is not { Length: 16 } second || first.SequenceEqual(second) ||
                first.Skip(12).Any(x => x != 0) || second.Skip(12).Any(x => x != 0))
                throw new IOException("Invalid automatic baseline: " + string.Join("; ", baseline.Errors));
            if (attempt < 7) delay();
        }
        throw new IOException("No coherent zero-override baseline within eight reads.");
    }

    private static void RequireParent(Process process, DateTime expectedStart)
    {
        try
        {
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime() != expectedStart)
                throw new InvalidOperationException("Parent process exited or its identity changed.");
        }
        catch (Exception e) when (e is not InvalidOperationException || e.Message != "Parent process exited or its identity changed.")
        {
            throw new InvalidOperationException("Parent process is no longer available.", e);
        }
    }

    private static void WriteAck(bool ok, string? error)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { ok, error }));
        Console.Out.Flush();
    }
}

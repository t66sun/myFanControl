using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using EcIdentityProbe;
using EcFeedbackProbe;
using EcPmcConfigProbe;

if (args.Length == 2 && args[0] == "--control-host" && int.TryParse(args[1], out int parentPid)) return ControlHost.Run(parentPid);
if (args.Length == 3 && args[0] == "--read-override-baseline" && args[1] == "--output") return OverrideBaselineProbe.Run(args[2]);

if (args.Length == 3 && args[0] == "--signed-rpm-trial" && args[1] == "--output") return SignedRpmTrialProbe.Run(args[2]);
if (args.Length == 3 && args[0] == "--rpm-trial" && args[1] == "--output") return RpmTrialProbe.Run(args[2]);
if (args.Length == 3 && args[0] == "--read-developer-query" && args[1] == "--output") return DeveloperQueryProbe.Run(args[2]);
if (args.Length == 3 && args[0] == "--recover-and-read-query" && args[1] == "--output") return DeveloperQueryProbe.Run(args[2], true);
if (args.Length == 3 && args[0] == "--measure-poll-delay" && args[1] == "--output") return PollTimerResolution.Measure(args[2]);

bool developerStatus = args.Length > 0 && args[0] is "--read-developer-status" or "--plan-developer-status";
bool requestStatus = developerStatus || (args.Length > 0 && args[0] is "--read-configuration-and-status" or "--plan-status");
var plan = new {
    Mode = "PNP PMC configuration reads only; no host PMC commands", Model = "21CX", Bios = "HYCN42WW", Chip = "5571", Ports = "4E/4F",
    ModuleSha256 = PnpSelectorIo.ExpectedModuleHash,
    DeveloperStatusModuleSha256 = developerStatus ? DeveloperStatusIo.ExpectedModuleHash : null,
    OptionalPmcStatusPort = requestStatus ? "6C one byte, after exact observed config gate" : null,
    DirectReads = new[] { "07 saved/restored", "20/21 chip before/after", "LDN 11/12: 30,60,61,62,63 twice" },
    IndirectIdentityReads = new[] { "2000/2001", "1060 before and after PNP transaction, must remain 00" },
    ConfigurationPayloadWrites = false, ActivationWrites = false, BaseAddressWrites = false,
    FanPayloadWrites = false, FirmwareModeChanges = false, PmcCommandWrites = false,
    AutomaticElevation = false, Selectors = "Only direct LDN 07 and identity indirect 10/11/2E, all saved/restored",
    PhysicalRpmVerified = false, ControlVerified = false
};
if (args.SequenceEqual(new[] { "--plan" }) || args.SequenceEqual(new[] { "--plan-status" }) || args.SequenceEqual(new[] { "--plan-developer-status" })) { Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true })); return 0; }
if (args.SequenceEqual(new[] { "--self-check" })) { SelfCheck.Run(); return 0; }
if (args.Length != 3 || args[0] is not ("--read-configuration" or "--read-configuration-and-status" or "--read-developer-status") || args[1] != "--output") return 64;
FileStream output;
try { output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 3; }
using (output)
{
    bool admin = false, attempted = false, initialized = false;
    var errors = new List<string>(); ConfigurationResult? result = null; IdentityResult? before = null, after = null; PmcStatusResult? statusResult = null;
    DateTimeOffset started = DateTimeOffset.UtcNow; long timestamp = Stopwatch.GetTimestamp();
    try
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
        if (bios?.GetValue("SystemProductName") as string != "21CX" || bios.GetValue("BIOSVersion") as string != "HYCN42WW")
            throw new NotSupportedException("Machine/BIOS profile mismatch; no device opened.");
        using var identity = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) throw new UnauthorizedAccessException("Administrator required; no device opened.");
        EcMutexWaitResult wait = new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
        if (wait.Status != EcMutexWaitStatus.Acquired || wait.Lease is null) throw new InvalidOperationException($"ISA mutex {wait.Status}; no device opened.");
        using var lease = wait.Lease;
        attempted = true; using var io = new PnpSelectorIo(); initialized = true;
        before = new IdentityReader(io).Read();
        if (before.RawChipId != 0x5571 || before.Mapping1060 != 0 || !before.SelectorsRestored || before.Errors.Count != 0)
            throw new InvalidOperationException("Indirect identity/mapping/restore gate failed.");
        result = new ConfigurationReader(io).Read();
        if (!result.Succeeded) throw new InvalidOperationException("PNP read/restoration failed; no more port access.");
        if (requestStatus) {
            statusResult = PmcStatusReader.Read(result, () => {
                if (!developerStatus) return io.PeekPmc2Status();
                using var statusIo = new DeveloperStatusIo();
                return statusIo.ReadStatus();
            });
            if (!statusResult.ReadCompleted) throw new InvalidOperationException("Status read failed: " + statusResult.Error);
        }
        after = new IdentityReader(io).Read();
        if (after.RawChipId != 0x5571 || after.Mapping1060 != 0 || !after.SelectorsRestored || after.Errors.Count != 0)
            throw new InvalidOperationException("Final indirect identity/mapping/restore gate failed.");
    }
    catch (Exception e) { errors.Add($"{e.GetType().Name}: {e.Message}"); }
    double elapsedMs = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    DateTimeOffset ended = DateTimeOffset.UtcNow;
    if (elapsedMs > 2000 || ended < started || (ended-started).TotalMilliseconds > 2000)
        errors.Add("Transaction timing invalid or exceeds two seconds; observation is not valid.");
    bool succeeded = result?.Succeeded == true && after is not null && errors.Count == 0;
    JsonSerializer.Serialize(output, new { SchemaVersion = 1, StartedUtc = started, CompletedUtc = ended, StopwatchElapsedMs = elapsedMs,
        Plan = plan, IsAdministrator = admin, DeviceOpenAttempted = attempted, PawnClientInitialized = initialized,
        BeforeIdentity = before, AfterIdentity = after, Result = result, Succeeded = succeeded, Pmc2Status = statusResult,
        PhysicalRpmVerified = false, ControlVerified = false, Errors = errors }, new JsonSerializerOptions { WriteIndented = true });
    return succeeded ? 0 : 2;
}

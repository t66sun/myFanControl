using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using EcIdentityProbe;
using EcFeedbackProbe;

const int IntervalMilliseconds = 500;
int sampleCount = args.Length > 0 && args[0] == "--sample-feedback-long" ? 60 : 20;
var plan = new {
    Mode = "Fixed SRAM feedback candidate reads; no speed control", Model = "21CX", Bios = "HYCN42WW", ChipId = "0x5571",
    Ports = "4E/4F", ModuleSha256 = PawnSelectorIo.ExpectedModuleHash,
    IdentityReads = new[] { "2000", "2001", "1060 (required 00 before SRAM, checked again after)" },
    SramReads = new[] { "0800-080F (low alias hypothesis)", "8800-880F (firmware XDATA candidates)" },
    ReadsPerBlock = 2, SampleCount = sampleCount, LongSampleCount = 60, IntervalMilliseconds,
    SelectorWrites = "Only 10/11 address selectors, saved/restored 2E; data selector 12 is never written",
    MappingChanges = false, FirmwareModeChanges = false, FanPayloadWrites = false, AutomaticElevation = false,
    PhysicalRpmVerified = false, ControlVerified = false,
    Interpretation = "Raw bytes and candidate big-endian words only; equality does not establish mapping, atomicity or RPM units."
};
if (args.SequenceEqual(new[] { "--self-check" })) { SelfCheck.Run(); return 0; }
if (args.SequenceEqual(new[] { "--plan" })) { Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true })); return 0; }
if (args.Length != 3 || args[0] is not ("--sample-feedback" or "--sample-feedback-long") || args[1] != "--output") {
    Console.Error.WriteLine("Use --plan, --self-check, or --sample-feedback --output <JSON path>, or --sample-feedback-long --output <JSON path>."); return 64;
}
// Establish writable output before opening devices; never self-elevate.
FileStream output;
try { output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None); }
catch (Exception e) { Console.Error.WriteLine($"Output unavailable; no device opened: {e.Message}"); return 3; }
using var outputLifetime = output;
var samples = new List<FeedbackResult>(); var errors = new List<string>();
bool administrator = false, deviceOpenAttempted = false, pawnClientInitialized = false;
Mutex? mutex = null; PawnSelectorIo? io = null;
try {
    using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
    if (bios?.GetValue("SystemProductName") as string != "21CX" || bios.GetValue("BIOSVersion") as string != "HYCN42WW")
        throw new NotSupportedException("Feedback probe is restricted to 21CX/HYCN42WW.");
    using var identity = WindowsIdentity.GetCurrent(); administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    if (!administrator) throw new UnauthorizedAccessException("Administrator required; no device opened. Automatic UAC is disabled.");
    mutex = new Mutex(false, @"Global\Access_ISABUS.HTP.Method");
    for (int i = 0; i < sampleCount; i++) {
        bool ownsMutex = false;
        try {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { ownsMutex = true; throw new InvalidOperationException("ISA mutex abandoned; sample aborted before port access."); }
            if (!ownsMutex) throw new TimeoutException("ISA mutex busy; sample aborted before port access.");
            if (io is null) { deviceOpenAttempted = true; io = new PawnSelectorIo(); pawnClientInitialized = true; }
            var sample = new FeedbackReader(io).Read(); samples.Add(sample);
            errors.AddRange(sample.Errors);
            if (!sample.ReadCompleted) throw new InvalidOperationException("Incomplete read; no further samples.");
        }
        finally { if (ownsMutex) mutex.ReleaseMutex(); }
        if (i + 1 < sampleCount) Thread.Sleep(IntervalMilliseconds); // Mutex released between samples.
    }
}
catch (Exception e) { errors.Add($"{e.GetType().Name}: {e.Message}"); }
finally { io?.Dispose(); mutex?.Dispose(); }
bool completed = samples.Count == sampleCount && samples.All(s => s.ReadCompleted) && errors.Count == 0;
JsonSerializer.Serialize(output, new { SchemaVersion = 1, CapturedUtc = DateTimeOffset.UtcNow, Plan = plan,
    IsAdministrator = administrator, DeviceOpenAttempted = deviceOpenAttempted, PawnClientInitialized = pawnClientInitialized,
    ReadCompleted = completed, PhysicalRpmVerified = false, ControlVerified = false, Samples = samples, Errors = errors },
    new JsonSerializerOptions { WriteIndented = true });
return completed ? 0 : 2;

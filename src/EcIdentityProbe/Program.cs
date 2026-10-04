using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using EcIdentityProbe;

if(args.SequenceEqual(new[]{"--self-check"})) { SelfCheck.Run(); return 0; }
var plan=new {
    Mode="EC identity only; no fan settings", Model="21CX", Bios="HYCN42WW", Ports="4E/4F", ModuleSha256=PawnSelectorIo.ExpectedModuleHash,
    AddressReads=new[]{"2000","2001","1060 only after 5570/5571 identity"},
    SelectorWrites="Indirect selectors 2E/2F, address selector 10/11 only; never write target data selector 12",
    Restoration="Save/read back selectors 10,11,2E; restore in finally; read back original values",
    RequiresAdministrator=true, AutomaticElevation=false, HardwareProtocolVerified=false
};
if(args.SequenceEqual(new[]{"--plan"})) { Console.WriteLine(JsonSerializer.Serialize(plan,new JsonSerializerOptions{WriteIndented=true})); return 0; }
if(args.Length!=3 || args[0]!="--read-identity" || args[1]!="--output") {
    Console.Error.WriteLine("Use --plan, --self-check, or --read-identity --output <JSON path>.");return 64;
}
// Establish output access before starting a hardware transaction.
using var output=File.Create(args[2]);
var errors=new List<string>();IdentityResult? result=null;bool administrator=false,deviceOpenAttempted=false,pawnClientInitialized=false;
Mutex? mutex=null;bool ownsMutex=false;
try {
    using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS",false);
    if(bios?.GetValue("SystemProductName") as string!="21CX" || bios.GetValue("BIOSVersion") as string!="HYCN42WW")
        throw new NotSupportedException("Identity probe is restricted to 21CX/HYCN42WW.");
    using var identity=WindowsIdentity.GetCurrent();administrator=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    if(!administrator)throw new UnauthorizedAccessException("Administrator required; no device opened. Automatic UAC is disabled.");
    mutex=new Mutex(false,@"Global\Access_ISABUS.HTP.Method");
    try { ownsMutex=mutex.WaitOne(TimeSpan.FromSeconds(2)); }
    catch(AbandonedMutexException) { ownsMutex=true;throw new InvalidOperationException("ISA mutex was abandoned; no device opened."); }
    if(!ownsMutex)throw new TimeoutException("ISA mutex busy; no device opened.");
    deviceOpenAttempted=true;using var io=new PawnSelectorIo();pawnClientInitialized=true;result=new IdentityReader(io).Read();
    errors.AddRange(result.Errors);
}
catch(Exception e) {errors.Add($"{e.GetType().Name}: {e.Message}");}
finally { if(ownsMutex)try { mutex!.ReleaseMutex(); } catch(Exception e) { errors.Add($"Mutex release failed: {e.Message}"); } mutex?.Dispose(); }
JsonSerializer.Serialize(output,new {SchemaVersion=1,CapturedUtc=DateTimeOffset.UtcNow,Plan=plan,IsAdministrator=administrator,DeviceOpenAttempted=deviceOpenAttempted,PawnClientInitialized=pawnClientInitialized,Result=result,Errors=errors},new JsonSerializerOptions{WriteIndented=true});
return result is { RawChipId:0x5570 or 0x5571, SelectorsRestored:true } && errors.Count==0 ? 0 : 2;

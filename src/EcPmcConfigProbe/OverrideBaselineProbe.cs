using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using EcFeedbackProbe;

namespace EcPmcConfigProbe;

internal static class OverrideBaselineProbe
{
    internal static int Run(string path)
    {
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        var errors=new List<string>(); FanOverrideBaselineResult? result=null;
        try {
            using var identity=WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator required.");
            using var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if(bios?.GetValue("SystemProductName") as string!="21CX" || bios.GetValue("BIOSVersion") as string!="HYCN42WW") throw new NotSupportedException("Machine/BIOS mismatch.");
            var wait=new GlobalIsaMutexGate().Acquire(TimeSpan.FromSeconds(2));
            if(wait.Status!=EcMutexWaitStatus.Acquired || wait.Lease is null) throw new IOException("ISA mutex unavailable.");
            PnpSelectorIo io;
            using(wait.Lease) io=new PnpSelectorIo();
            using(io) {
                var reader=new FanOverrideBaselineReader(io);
                for(int i=0;i<8;i++) {
                    result=reader.Read();
                    if(result.Available) break;
                    if(!result.SelectorsRestored || result.ChipIdBefore!=0x5571 || result.ChipIdAfter!=0x5571 ||
                        result.Mapping1060Before!=0 || result.Mapping1060After!=0 ||
                        !(result.Errors.Count==1 && result.FirstBytes is {Length:16} a && result.SecondBytes is {Length:16} b && !a.SequenceEqual(b)))
                        throw new IOException("Invalid baseline: "+string.Join("; ",result.Errors));
                    Thread.Sleep(100);
                }
                if(result?.Available!=true) throw new IOException("No coherent baseline within eight reads.");
            }
        } catch(Exception e) {errors.Add(e.Message);}
        JsonSerializer.Serialize(output,new {CapturedUtc=DateTimeOffset.UtcNow,Succeeded=errors.Count==0,Result=result,Errors=errors},new JsonSerializerOptions {WriteIndented=true});
        return errors.Count==0?0:2;
    }
}

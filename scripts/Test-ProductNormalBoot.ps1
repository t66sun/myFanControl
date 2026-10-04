[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9-]+$')][string]$DirectoryName='myFanControl')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
$taskDir=Join-Path $taskRoot ('artifacts/diagnostics/product-normal-boot-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskDir | Out-Null
$taskReport=[ordered]@{CapturedUtc=[DateTime]::UtcNow.ToString('o');ProductDirectory=$DirectoryName;NormalBootVerified=$false;ProductControlVerified=$false;Error=$null}
try {
 Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ThinkBookFinalBoot {
 [StructLayout(LayoutKind.Sequential)] public struct Ci { public uint Length; public uint Options; }
 [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ref Ci info, uint size, out uint returned);
 public static uint Read() { var i=new Ci{Length=8}; uint returned; if(NtQuerySystemInformation(103,ref i,8,out returned)!=0 || returned!=8)throw new Exception("CI read failed"); return i.Options; }
}
'@
 $taskOptions=[ThinkBookFinalBoot]::Read()
 $taskReport.CodeIntegrityOptions=$taskOptions.ToString('X8')
 if(($taskOptions -band 2) -ne 0 -or ($taskOptions -band 0x400) -eq 0){throw 'Normal non-test boot with HVCI must be active.'}
 if((Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' -Name VulnerableDriverBlocklistEnable) -ne 1){throw 'Driver blocklist baseline differs.'}
 $taskPath=Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO' -Name ImagePath
 if($taskPath -ne '\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys' -or (Get-Service PawnIO).Status -ne 'Running'){throw 'Original driver baseline differs.'}
 if((Get-FileHash -LiteralPath (Join-Path $env:SystemRoot $taskPath.Substring(12))).Hash -ne 'FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940'){throw 'Original driver hash mismatch.'}
 $taskReport.NormalBootVerified=$true
 $taskExe=Join-Path $taskRoot ('artifacts/publish/'+$DirectoryName+'/myFanControl.exe')
 $taskOutput=Join-Path $taskDir 'control-product.json'
 $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--control-test','--output',('"'+$taskOutput+'"')) -WindowStyle Hidden -Wait -PassThru
 if($taskProcess.ExitCode -ne 0 -or -not (Get-Content -LiteralPath $taskOutput -Raw | ConvertFrom-Json).Succeeded){throw 'Normal boot product control verification failed.'}
 $taskReport.ProductControlVerified=$true
} catch {$taskReport.Error=$_.Exception.Message}
finally {$taskReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDir 'report.json') -Encoding utf8; Write-Output ('Report: '+$taskDir)}
if($taskReport.Error){Write-Error $taskReport.Error -ErrorAction Continue;exit 1}

[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/acpi'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not ('ThinkBook.FirmwareReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ThinkBook {
    public static class FirmwareReader {
        private const uint ACPI = 0x41435049;
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern uint EnumSystemFirmwareTables(uint provider, IntPtr buffer, uint size);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint id, IntPtr buffer, uint size);
        public static uint[] Enumerate() {
            uint size = EnumSystemFirmwareTables(ACPI, IntPtr.Zero, 0);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try {
                uint actual = EnumSystemFirmwareTables(ACPI, buffer, size);
                if (actual == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (actual > size || actual % 4 != 0) throw new InvalidOperationException("Invalid enumeration size");
                uint[] ids = new uint[actual / 4];
                for(int i = 0; i < ids.Length; i++) ids[i] = unchecked((uint)Marshal.ReadInt32(buffer, i * 4));
                return ids;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        public static byte[] Read(uint id) {
            uint size = GetSystemFirmwareTable(ACPI, id, IntPtr.Zero, 0);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (size > 16 * 1024 * 1024) throw new InvalidOperationException("Unexpected firmware table size");
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try {
                uint actual = GetSystemFirmwareTable(ACPI, id, buffer, size);
                if (actual == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (actual > size) throw new InvalidOperationException("Firmware table changed size");
                byte[] bytes = new byte[actual];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return bytes;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
'@
}
$ids = [ThinkBook.FirmwareReader]::Enumerate()
# Some firmware omits DSDT from enumeration although explicit retrieval works.
$dsdtId = [BitConverter]::ToUInt32([Text.Encoding]::ASCII.GetBytes('DSDT'), 0)
if ($dsdtId -notin $ids) { $ids = @($ids) + $dsdtId }
$results = foreach ($group in ($ids | Group-Object)) {
    $id = [uint32]$group.Name
    $signature = [Text.Encoding]::ASCII.GetString([BitConverter]::GetBytes($id))
    # Only ACPI code tables are exported, not licensing or SMBIOS data.
    if ($signature -notin @('DSDT', 'SSDT', 'FACP')) { continue }
    $bytes = [ThinkBook.FirmwareReader]::Read($id)
    if ($bytes.Length -lt 36) { throw "Invalid $signature header" }
    $headerSignature = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
    $headerLength = [BitConverter]::ToUInt32($bytes, 4)
    if ($headerSignature -ne $signature -or $headerLength -ne $bytes.Length) { throw "Invalid $signature length/signature" }
    $sum = 0
    foreach ($b in $bytes) { $sum = ($sum + $b) -band 255 }
    if ($sum -ne 0) { throw "Invalid $signature checksum" }
    $path = Join-Path $OutputDirectory "$signature.dat"
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($path), $bytes)
    [pscustomobject]@{
        Signature = $signature
        Bytes = $bytes.Length
        OemId = [Text.Encoding]::ASCII.GetString($bytes, 10, 6).Trim()
        OemTableId = [Text.Encoding]::ASCII.GetString($bytes, 16, 8).Trim()
        EnumeratedCount = $group.Count
        ExportedCount = 1
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Limitation = if ($group.Count -gt 1) { 'Windows API returns only the first table for duplicate signatures.' } else { $null }
    }
}
[pscustomobject]@{
    CapturedUtc = [DateTime]::UtcNow.ToString('o')
    ReadOnly = $true
    Api = 'EnumSystemFirmwareTables / GetSystemFirmwareTable'
    Tables = @($results)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8
$results | Format-Table Signature,Bytes,EnumeratedCount,ExportedCount

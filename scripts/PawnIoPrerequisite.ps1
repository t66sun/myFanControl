param([string]$InstallerPath, [string]$ExpectedHash)
$ErrorActionPreference='Stop'

function Get-PawnIoPrerequisiteDecision([bool]$Registered,[bool]$DriverExists,[string]$Version,[string]$SignatureStatus,[string]$SignerSubject) {
    if(-not $Registered -or -not $DriverExists){return 10}
    $parsedVersion=$null
    if($SignatureStatus -ne 'Valid' -or $SignerSubject -notmatch '(^|,\s*)CN=Microsoft Windows Hardware Compatibility Publisher(,|$)' -or -not [version]::TryParse($Version,[ref]$parsedVersion)){return 20}
    if($parsedVersion -ge [version]'2.2'){return 0}
    return 10
}

function Test-PawnIoInstallerTrust([string]$ActualHash,[string]$ExpectedHash,[string]$SignatureStatus) {
    return $ExpectedHash -match '^[0-9a-fA-F]{64}$' -and $ActualHash -eq $ExpectedHash -and $SignatureStatus -eq 'Valid'
}

if($MyInvocation.InvocationName -ne '.') {
    try {
        if($InstallerPath) {
            $signature=Get-AuthenticodeSignature -LiteralPath $InstallerPath
            if(Test-PawnIoInstallerTrust (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash $ExpectedHash ([string]$signature.Status)){exit 0}
            exit 20
        }
        $registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
        try {
            $service=$registry.OpenSubKey('SYSTEM\CurrentControlSet\Services\PawnIO')
            if($null -eq $service){exit 10}
            try {$driverPath=[Environment]::ExpandEnvironmentVariables([string]$service.GetValue('ImagePath')).Trim('"')} finally {$service.Dispose()}
        } finally {$registry.Dispose()}
        if($driverPath.StartsWith('\??\')){$driverPath=$driverPath.Substring(4)}
        if($driverPath.StartsWith('\SystemRoot\',[StringComparison]::OrdinalIgnoreCase)){$driverPath=$env:SystemRoot+$driverPath.Substring(11)}
        if(-not (Test-Path -LiteralPath $driverPath -PathType Leaf)){exit 10}
        $signature=Get-AuthenticodeSignature -LiteralPath $driverPath
        $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($driverPath).FileVersion
        exit (Get-PawnIoPrerequisiteDecision $true $true $version ([string]$signature.Status) ([string]$signature.SignerCertificate.Subject))
    } catch {Write-Error $_ -ErrorAction Continue; exit 20}
}

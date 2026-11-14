$script = $PSCommandPath
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $script + '"')
    exit
}
$dir = $PSScriptRoot
$mapper = Join-Path $dir 'zxmapper.exe'
$sys = Join-Path $dir 'kernel-mode.sys'
try {
    Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class DevOpen{[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr CreateFileW(string n,int a,int s,IntPtr se,int c,int f,IntPtr t);[DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr h);}'
    function Test-Device {
        $handle = [DevOpen]::CreateFileW('\\.\WhReader4', -1073741824, 3, [IntPtr]::Zero, 3, 128, [IntPtr]::Zero)
        if ($handle -eq [IntPtr](-1)) { return $false }
        [DevOpen]::CloseHandle($handle) | Out-Null
        return $true
    }
    if (Test-Device) {
        Write-Host 'Driver already loaded. You can start the game and run 2-overlay.bat.'
        Read-Host 'Press Enter to close'
        exit 0
    }
    if (!(Test-Path $mapper)) { throw ('mapper not found: ' + $mapper) }
    if (!(Test-Path $sys)) { throw ('driver not found: ' + $sys) }
    Write-Host ('Mapping driver: ' + $sys)
    $output = (& $mapper ('"' + $sys + '"') 2>&1 | Out-String).Trim()
    Write-Host $output
    if (!(Test-Device)) { throw 'Driver device did not open after mapping.' }
    Write-Host 'Driver loaded. Now start the game and run 2-overlay.bat.'
    Read-Host 'Press Enter to close'
} catch {
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

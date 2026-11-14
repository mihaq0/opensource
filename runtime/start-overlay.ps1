$script = $PSCommandPath
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $script + '"')
    exit
}
$dir = $PSScriptRoot
$exe = Join-Path $dir 'NightRun.exe'
try {
    if (!(Test-Path $exe)) { throw ('NightRun.exe not found: ' + $exe) }
    Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class DevOpen{[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr CreateFileW(string n,int a,int s,IntPtr se,int c,int f,IntPtr t);[DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr h);}'
    $handle = [DevOpen]::CreateFileW('\\.\WhReader4', -1073741824, 3, [IntPtr]::Zero, 3, 128, [IntPtr]::Zero)
    if ($handle -eq [IntPtr](-1)) { throw 'Driver is not loaded. Run 1-driver.bat first (game must be closed).' }
    [DevOpen]::CloseHandle($handle) | Out-Null
    $game = Get-Process -Name 'ROB RUN' -ErrorAction SilentlyContinue
    if (!$game) { Write-Host 'Warning: ROB RUN is not running. Start the game, the overlay will show OFFLINE until then.' -ForegroundColor Yellow }
    foreach ($instance in (Get-Process -Name NightRun -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $instance.Id -ErrorAction Stop
        $instance.WaitForExit(10000) | Out-Null
    }
    if (Get-Process -Name NightRun -ErrorAction SilentlyContinue) { throw 'Old NightRun did not exit.' }
    $new = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
    Write-Host ('NightRun started, PID ' + $new.Id)
    Start-Sleep -Seconds 3
    if ($new.HasExited) { throw ('NightRun exited immediately, code ' + $new.ExitCode) }
    Write-Host 'Overlay is running. Keep the game window focused to see ESP.'
    Read-Host 'Press Enter to close'
} catch {
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

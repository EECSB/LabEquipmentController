#requires -Version 5
<#
.SYNOPSIS
    Smoke-tests the compiled Lab Equipment Controller installer end to end.

.DESCRIPTION
    Silently installs LabEquipmentController-v<Version>-setup.exe, verifies that the app
    files, the Start Menu shortcut, the uninstaller and the Programs-and-Features entry
    were all created, launches the installed app to prove the payload actually runs, then
    silently uninstalls and confirms everything was removed again.

    No elevation needed: the app requires no administrator rights, so the installer
    installs per-user into %LocalAppData%\Programs by default and this script tests that
    path. It changes nothing permanently — the app is always uninstalled before the
    script returns, and the user's settings under %AppData% are left alone by design.

    The payload is framework-dependent, so the launch check is also the check that the
    .NET 10 Desktop Runtime is present on this machine. If it is not, the launch fails
    here exactly as it would for a user, which is the point.

.PARAMETER Version
    Version embedded in the setup file name (bin\LabEquipmentController-v<Version>-setup.exe).
    Defaults to 1.0.0.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\Test-Installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

# installer\ sits next to bin\ under the repo root, so resolve paths from here.
$repoRoot = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $repoRoot "bin\LabEquipmentController-v$Version-setup.exe"
$app = Join-Path $env:LOCALAPPDATA 'Programs\LabEquipmentController'
$exe = Join-Path $app 'LabEquipmentController.exe'
$uninstaller = Join-Path $app 'unins000.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lab Equipment Controller\Lab Equipment Controller.lnk'
$installLog = Join-Path $env:TEMP 'LabEquipmentController-inno-install.log'

if (-not (Test-Path $setup))
{
    throw "Installer not found: $setup. Build it first by compiling installer\LabEquipmentController.iss."
}

$script:passed = $true
function Check($label, $condition)
{
    $status = 'FAIL'
    if ($condition)
    {
        $status = 'ok'
    }
    else
    {
        $script:passed = $false
    }
    Write-Host ("  [{0,-4}] {1}" -f $status, $label)
}

Write-Host "Installing $setup ..."
$install = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$installLog" -Wait -PassThru

# Let the installer finish writing files before checking.
$deadline = (Get-Date).AddSeconds(60)
while ((-not (Test-Path $exe)) -and (Get-Date) -lt $deadline)
{
    Start-Sleep -Milliseconds 500
}

Write-Host 'Verifying install:'
Check 'installer exited 0' ($install.ExitCode -eq 0)
Check 'app folder created' (Test-Path $app)
Check 'LabEquipmentController.exe present' (Test-Path $exe)
Check 'WebView2 loader present' (Test-Path (Join-Path $app 'WebView2Loader.dll'))
Check 'Start Menu shortcut created' (Test-Path $shortcut)
Check 'uninstaller present' (Test-Path $uninstaller)

# The point of the framework-dependent payload is a small installer; the price is that the
# footprint claim is only true if the build really is framework-dependent. A self-contained
# build would drag its runtime in and land far over this.
if (Test-Path $exe)
{
    $sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
    Check "payload is the small build ($sizeMb MB, under 25)" ($sizeMb -lt 25)
}

$arpKeys = @(
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
$arp = foreach ($key in $arpKeys)
{
    if (Test-Path $key)
    {
        Get-ChildItem $key |
            ForEach-Object { Get-ItemProperty $_.PSPath } |
            Where-Object { $_.DisplayName -like '*Lab Equipment Controller*' }
    }
}
Check 'Programs-and-Features entry registered' ([bool]$arp)
if ($arp)
{
    $entry = $arp | Select-Object -First 1
    Write-Host ("       {0} / {1} / {2}" -f $entry.DisplayName, $entry.DisplayVersion, $entry.Publisher)
}

Write-Host 'Launching the installed app:'
if (Test-Path $exe)
{
    $app_proc = Start-Process $exe -PassThru
    # A missing runtime kills the process within a second or two; a working one has the
    # main window up well inside this.
    $deadline = (Get-Date).AddSeconds(20)
    while ((-not $app_proc.HasExited) -and ($app_proc.MainWindowHandle -eq 0) -and (Get-Date) -lt $deadline)
    {
        Start-Sleep -Milliseconds 500
        $app_proc.Refresh()
    }

    Check 'process still running (runtime resolved)' (-not $app_proc.HasExited)
    Check 'main window opened' ($app_proc.MainWindowHandle -ne 0)

    try { $app_proc | Stop-Process -Force -ErrorAction Stop } catch {}
    Start-Sleep -Milliseconds 1500
}

Write-Host 'Uninstalling ...'
if (Test-Path $uninstaller)
{
    # The Inno uninstaller relaunches itself from a temp copy, so poll for removal.
    Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    $deadline = (Get-Date).AddSeconds(45)
    while ((Test-Path $app) -and (Get-Date) -lt $deadline)
    {
        Start-Sleep -Milliseconds 500
    }
}
Check 'app folder removed' (-not (Test-Path $app))
Check 'Start Menu shortcut removed' (-not (Test-Path $shortcut))

Write-Host ''
if ($script:passed)
{
    Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
    exit 0
}
else
{
    Write-Host 'SMOKE TEST FAILED' -ForegroundColor Red
    exit 1
}

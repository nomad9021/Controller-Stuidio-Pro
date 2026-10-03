# Controller Studio Pro installer for Windows 10 / 11.
#   irm https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.ps1 | iex
# Uninstall:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.ps1))) -Uninstall
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"  # Invoke-WebRequest is very slow with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$ExeUrl    = if ($env:CSP_EXE_URL) { $env:CSP_EXE_URL } else { "https://github.com/nomad9021/Controller-Stuidio-Pro/releases/latest/download/ControllerStudioPro.exe" }
$ViGEmUrl  = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"
$Base      = Join-Path $env:LOCALAPPDATA "ControllerStudioPro"
$Exe       = Join-Path $Base "ControllerStudioPro.exe"
$StartMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Controller Studio Pro.lnk"
$RunKey    = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RunName   = "Controller Studio Pro"
$Tmp       = Join-Path $env:TEMP "controller-studio-pro-install"

function Say($m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "xx  $m" -ForegroundColor Red; throw $m }

function Stop-App {
    # The exe can't be replaced until it has fully exited.
    Get-Process ControllerStudioPro -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru -ErrorAction SilentlyContinue |
        Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    # The old Python version
    Get-CimInstance Win32_Process -Filter "Name='pythonw.exe' OR Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*controllerstudio*" } |
        ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName Terminate -ErrorAction SilentlyContinue | Out-Null }
    Start-Sleep -Milliseconds 700
}

# Versions before 2.0 were a Python service with a web view. Presets carry over
# (%APPDATA%\controller-studio-pro is shared); everything else goes.
function Remove-PythonVersion {
    $old = @((Join-Path $Base "app"), (Join-Path $Base "venv"), (Join-Path $env:LOCALAPPDATA "controller-studio-pro"))
    $oldStartup = Join-Path ([Environment]::GetFolderPath("Startup")) "Controller Studio Pro service.lnk"
    if (($old + $oldStartup | Where-Object { Test-Path $_ }).Count -gt 0) {
        Say "Removing the old Python version (your presets stay)"
        foreach ($p in $old + $oldStartup) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Install-ViGEm {
    if (Get-Service ViGEmBus -ErrorAction SilentlyContinue) { Say "ViGEmBus driver already installed"; return }
    Say "Installing the ViGEmBus driver for the virtual controller (Windows will ask for permission)"
    try {
        $setup = Join-Path $Tmp "ViGEmBus_setup.exe"
        Invoke-WebRequest $ViGEmUrl -OutFile $setup
        Start-Process $setup -ArgumentList "/exenoui", "/qn", "/norestart" -Verb RunAs -Wait
    } catch {
        Warn "Skipped ViGEmBus ($($_.Exception.Message)). Trigger effects still work; the virtual controller needs it."
        Warn "Run this installer again later to add it."
    }
}

function Fetch-App {
    Say "Downloading Controller Studio Pro"
    $download = Join-Path $Tmp "ControllerStudioPro.exe"
    Invoke-WebRequest $ExeUrl -OutFile $download
    if ((Get-Item $download).Length -lt 1MB) { Die "The download looks incomplete. Try again in a minute." }
    Unblock-File $download
    Move-Item $download $Exe -Force
}

function New-Shortcut {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($StartMenu)
    $lnk.TargetPath = $Exe
    $lnk.WorkingDirectory = $Base
    $lnk.Description = "Adaptive trigger presets, lighting and a controller tester for DualSense"
    $lnk.Save()
}

function Do-Uninstall {
    Say "Removing Controller Studio Pro"
    Stop-App
    Remove-Item $StartMenu -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty $RunKey -Name $RunName -ErrorAction SilentlyContinue
    foreach ($name in "SDL_GAMECONTROLLER_IGNORE_DEVICES", "SDL_JOYSTICK_HIDAPI_PS5") {
        [Environment]::SetEnvironmentVariable($name, $null, "User")
    }
    Remove-PythonVersion
    Remove-Item $Base -Recurse -Force -ErrorAction SilentlyContinue
    Say "Done. Your presets are still in $env:APPDATA\controller-studio-pro (delete it to remove them too)."
    Say "The ViGEmBus driver stays installed; remove it from Settings > Apps if nothing else uses it."
}

function Do-Install {
    New-Item -ItemType Directory -Force -Path $Tmp, $Base | Out-Null
    Install-ViGEm
    Stop-App
    Remove-PythonVersion
    Fetch-App
    Say "Adding Controller Studio Pro to the Start menu and starting it with Windows"
    New-Shortcut
    if (-not (Test-Path $RunKey)) { New-Item $RunKey | Out-Null }  # never -Force: that would empty the key
    Set-ItemProperty $RunKey -Name $RunName -Value "`"$Exe`" --background"
    Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
    Start-Process $Exe
    Say "Installed! Controller Studio Pro is open, and stays in the notification area when you close it."
}

if ([Environment]::OSVersion.Platform -ne "Win32NT") { Die "This installer is for Windows. On Linux use install.sh." }
if ($Uninstall -or $env:CSP_UNINSTALL -eq "1") { Do-Uninstall } else { Do-Install }

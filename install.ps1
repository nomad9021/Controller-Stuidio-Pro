# Controller Studio Pro installer for Windows 10 / 11.
#   irm https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.ps1 | iex
# Uninstall:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.ps1))) -Uninstall
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"  # Invoke-WebRequest is very slow with the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Zip      = "https://github.com/nomad9021/Controller-Stuidio-Pro/archive/refs/heads/main.zip"
$PyUrl    = "https://www.python.org/ftp/python/3.13.7/python-3.13.7-amd64.exe"
$ViGEmUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"
$Base     = Join-Path $env:LOCALAPPDATA "ControllerStudioPro"
$AppDir   = Join-Path $Base "app"
$Venv     = Join-Path $Base "venv"
$Pythonw  = Join-Path $Venv "Scripts\pythonw.exe"
$StartMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Controller Studio Pro.lnk"
$Startup  = Join-Path ([Environment]::GetFolderPath("Startup")) "Controller Studio Pro service.lnk"
$Tmp      = Join-Path $env:TEMP "controller-studio-pro-install"

function Say($m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "xx  $m" -ForegroundColor Red; throw $m }

function Stop-App {
    Get-CimInstance Win32_Process -Filter "Name='pythonw.exe' OR Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*controllerstudio*" } |
        ForEach-Object { Invoke-CimMethod -InputObject $_ -MethodName Terminate | Out-Null }
    Start-Sleep -Milliseconds 500
}

function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
}

function Has-Winget { [bool](Get-Command winget -ErrorAction SilentlyContinue) }

# A Python 3.11 - 3.14 interpreter (pythonnet, which draws the window, needs 3.11+).
function Find-Python {
    $candidates = @()
    if (Get-Command py -ErrorAction SilentlyContinue) {
        foreach ($v in "3.13", "3.12", "3.14", "3.11") { $candidates += ,@("py", "-$v") }
    }
    $candidates += ,@("python")
    foreach ($dir in Get-ChildItem (Join-Path $env:LOCALAPPDATA "Programs\Python") -Directory -ErrorAction SilentlyContinue) {
        $candidates += ,@((Join-Path $dir.FullName "python.exe"))
    }
    foreach ($c in $candidates) {
        $exe = $c[0]; $rest = @($c | Select-Object -Skip 1)
        try {
            # The Microsoft Store "python" alias prints nothing useful, so check the real answer.
            $out = & $exe @rest -c "import sys; print('%d.%d|%s' % (sys.version_info[:2] + (sys.executable,)))" 2>$null
        } catch { continue }
        if ($LASTEXITCODE -ne 0 -or -not $out) { continue }
        $ver, $path = "$out".Trim().Split("|", 2)
        if ([version]$ver -ge [version]"3.11" -and [version]$ver -lt [version]"3.15") { return $path }
    }
    return $null
}

function Install-Python {
    $py = Find-Python
    if ($py) { Say "Using Python at $py"; return $py }
    Say "Installing Python 3.13 (just for you, no admin needed)"
    if (Has-Winget) {
        winget install -e --id Python.Python.3.13 --scope user --silent --accept-package-agreements --accept-source-agreements | Out-Host
    }
    Refresh-Path
    $py = Find-Python
    if (-not $py) {
        $exe = Join-Path $Tmp "python-setup.exe"
        Invoke-WebRequest $PyUrl -OutFile $exe
        Start-Process $exe -ArgumentList "/quiet", "InstallAllUsers=0", "PrependPath=1", "Include_launcher=1", "Include_test=0" -Wait
        Refresh-Path
        $py = Find-Python
    }
    if (-not $py) { Die "Couldn't install Python. Install Python 3.13 from python.org, then run this again." }
    return $py
}

function Install-WebView2 {
    $key = "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    $found = (Test-Path "HKLM:\$key") -or (Test-Path "HKCU:\$($key -replace 'WOW6432Node\\', '')")
    if ($found) { return }
    Say "Installing the Microsoft Edge WebView2 runtime (draws the app window)"
    if (Has-Winget) {
        winget install -e --id Microsoft.EdgeWebView2Runtime --silent --accept-package-agreements --accept-source-agreements | Out-Host
    } else {
        $exe = Join-Path $Tmp "webview2.exe"
        Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $exe
        Start-Process $exe -ArgumentList "/silent", "/install" -Wait
    }
}

function Install-ViGEm {
    if (Get-Service ViGEmBus -ErrorAction SilentlyContinue) { Say "ViGEmBus driver already installed"; return }
    Say "Installing the ViGEmBus driver for the virtual controller (Windows will ask for permission)"
    try {
        $exe = Join-Path $Tmp "ViGEmBus_setup.exe"
        Invoke-WebRequest $ViGEmUrl -OutFile $exe
        Start-Process $exe -ArgumentList "/exenoui", "/qn", "/norestart" -Verb RunAs -Wait
    } catch {
        Warn "Skipped ViGEmBus ($($_.Exception.Message)). Trigger effects still work; the virtual controller needs it."
        Warn "Run this installer again later to add it."
    }
}

function Fetch-App {
    Say "Downloading Controller Studio Pro to $AppDir"
    $zipFile = Join-Path $Tmp "app.zip"
    Invoke-WebRequest $Zip -OutFile $zipFile
    $unpacked = Join-Path $Tmp "unpacked"
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    Expand-Archive $zipFile -DestinationPath $unpacked -Force
    $src = Get-ChildItem $unpacked -Directory | Select-Object -First 1
    if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }
    Move-Item $src.FullName $AppDir
}

function Install-Packages($py) {
    if (-not (Test-Path $Pythonw)) {
        Say "Creating the Python environment"
        & $py -m venv $Venv
        if ($LASTEXITCODE -ne 0) { Die "Creating the Python environment failed." }
    }
    $vpy = Join-Path $Venv "Scripts\python.exe"
    Say "Installing Python packages (hidapi, pywebview)"
    & $vpy -m pip install --disable-pip-version-check -q --upgrade pip | Out-Host
    & $vpy -m pip install --disable-pip-version-check -q --upgrade hidapi pywebview | Out-Host
    if ($LASTEXITCODE -ne 0) { Die "Installing Python packages failed." }

    # vgamepad only ships as source, and building it pops up an old ViGEmBus installer,
    # so copy its Python files and ViGEmClient.dll straight into the environment instead.
    $site = Join-Path $Venv "Lib\site-packages"
    if (-not (Test-Path (Join-Path $site "vgamepad"))) {
        Say "Adding vgamepad (virtual controller bindings)"
        $meta = Invoke-RestMethod "https://pypi.org/pypi/vgamepad/0.1.0/json"
        $sdist = $meta.urls | Where-Object { $_.packagetype -eq "sdist" } | Select-Object -First 1
        $tgz = Join-Path $Tmp "vgamepad.tar.gz"
        Invoke-WebRequest $sdist.url -OutFile $tgz
        tar -xzf $tgz -C $Tmp
        Copy-Item (Join-Path $Tmp "vgamepad-0.1.0\vgamepad") $site -Recurse -Force
    }
}

function New-Shortcut($path, $arguments, $description) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $Pythonw
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $AppDir
    $lnk.IconLocation = (Join-Path $AppDir "assets\controller-studio-pro.ico")
    $lnk.Description = $description
    $lnk.Save()
}

function Do-Uninstall {
    Say "Removing Controller Studio Pro"
    Stop-App
    Remove-Item $StartMenu, $Startup -Force -ErrorAction SilentlyContinue
    foreach ($name in "SDL_GAMECONTROLLER_IGNORE_DEVICES", "SDL_JOYSTICK_HIDAPI_PS5") {
        [Environment]::SetEnvironmentVariable($name, $null, "User")
    }
    Remove-Item $Base -Recurse -Force -ErrorAction SilentlyContinue
    Say "Done. Your presets are still in $env:APPDATA\controller-studio-pro (delete it to remove them too)."
    Say "The ViGEmBus driver stays installed; remove it from Settings > Apps if nothing else uses it."
}

function Do-Install {
    New-Item -ItemType Directory -Force -Path $Tmp, $Base | Out-Null
    $py = Install-Python
    Install-WebView2
    Install-ViGEm
    Stop-App
    Fetch-App
    Install-Packages $py

    Say "Adding Controller Studio Pro to the Start menu and starting the background service"
    New-Shortcut $StartMenu "-m controllerstudio.winwindow" "Adaptive trigger presets, lighting and a controller tester for DualSense"
    New-Shortcut $Startup "-m controllerstudio.server" "Controller Studio Pro background service"
    Start-Process $Pythonw -ArgumentList "-m", "controllerstudio.server" -WorkingDirectory $AppDir -WindowStyle Hidden
    Remove-Item $Tmp -Recurse -Force -ErrorAction SilentlyContinue
    Say "Installed! Open Controller Studio Pro from the Start menu."
}

if ([Environment]::OSVersion.Platform -ne "Win32NT") { Die "This installer is for Windows. On Linux use install.sh." }
if ($Uninstall -or $env:CSP_UNINSTALL -eq "1") { Do-Uninstall } else { Do-Install }

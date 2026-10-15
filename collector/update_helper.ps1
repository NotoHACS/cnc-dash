# CncDash collector self-update helper.
# Downloads the latest GitHub release (collector-win-x64.zip or collector-win-x86.zip
# per the machine's architecture, + .sha256), verifies it,
# swaps binaries in place (preserving config_local.json + state.json), restarts the
# NSSM service, and rolls back if the service fails to come up.
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$ServiceName
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ExeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Log = Join-Path $ExeDir "update.log"
$VersionFile = Join-Path $ExeDir "version.txt"
$Rid = if ($env:PROCESSOR_ARCHITECTURE -eq "x86" -and -not $env:PROCESSOR_ARCHITEW6432) { "win-x86" } else { "win-x64" }
$ZipName = "collector-$Rid.zip"

function Log($msg) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $msg"
    Write-Host $line
    Add-Content -Path $Log -Value $line
}

function Stop-ServiceSafe {
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if ($nssm) {
        $status = nssm status $ServiceName 2>$null
        if ($status -eq $null -or $status -like "*SERVICE_NOT_INSTALLED*") { return $false }
        nssm stop $ServiceName | Out-Null
        return $true
    }
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) { Stop-Service -Name $ServiceName -Force; return $true }
    return $false
}

function Start-ServiceSafe {
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if ($nssm) { nssm start $ServiceName | Out-Null; return }
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function ServiceRunning {
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if ($nssm) { return (nssm status $ServiceName 2>$null) -eq "SERVICE_RUNNING" }
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    return $svc -and $svc.Status -eq "Running"
}

# The collector exe holds itself open while running (service or console);
# copying over a running exe always fails. Wait up to 10s for exit, then
# force-kill: safe — the service was already stopped (NSSM does not auto-restart).
function Wait-CollectorExit {
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name "collector" -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    Log "WARNING: collector still running after stop; force-killing"
    Get-Process -Name "collector" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    return (-not (Get-Process -Name "collector" -ErrorAction SilentlyContinue))
}

function Copy-WithRetry($src, $dst) {
    for ($i = 0; $i -lt 3; $i++) {
        try { Copy-Item $src $dst -Force; return $true }
        catch {
            if ($i -eq 2) { return $false }
            Start-Sleep -Milliseconds 500
        }
    }
    return $false
}

# copy → rename-over → (update.bat only) staged fallback, never fatal on the bat
function Swap-File($stageDir, $name) {
    $newFile = Join-Path $stageDir $name
    if (-not (Test-Path $newFile)) { return $true }
    $dst = Join-Path $ExeDir $name
    if (Copy-WithRetry $newFile $dst) { return $true }
    try { Move-Item -Path $newFile -Destination $dst -Force; return $true } catch {}
    if ($name -ieq "update.bat") {
        try {
            Copy-Item $newFile (Join-Path $ExeDir "update.bat.new") -Force
            Log "update.bat locked by cmd.exe; staged update.bat.new (consumed next run)"
            return $true
        } catch { return $false }
    }
    return $false
}

Log "=== update run ==="
Log "repo=$Repo service=$ServiceName"

# 1. latest release
try {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "cncdash-collector" }
} catch {
    Log "ERROR: cannot query releases: $($_.Exception.Message)"; exit 1
}
$tag = $rel.tag_name
Log "latest release: $tag"

$current = "dev"
if (Test-Path $VersionFile) { $current = (Get-Content $VersionFile -Raw).Trim() }
if ($current -eq $tag) { Log "already up to date ($current). nothing to do."; exit 0 }

# 2. locate download assets
$zipAsset = $rel.assets | Where-Object { $_.name -eq $ZipName } | Select-Object -First 1
$shaAsset = $rel.assets | Where-Object { $_.name -eq "$ZipName.sha256" } | Select-Object -First 1
if (-not $zipAsset) { Log "ERROR: asset $ZipName not found in release $tag"; exit 1 }

$tmp = Join-Path $env:TEMP "cncdash-update-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tmp | Out-Null

# 3. download + checksum verify
try {
    $zipPath = Join-Path $tmp $ZipName
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -UseBasicParsing
    if ($shaAsset) {
        $expected = ((Invoke-WebRequest -Uri $shaAsset.browser_download_url -UseBasicParsing).Content.Trim().Split(" ")[0]).ToLower()
        $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
        if ($expected -ne $actual) { throw "checksum mismatch: expected $expected got $actual" }
        Log "sha256 verified"
    } else {
        Log "WARNING: no checksum asset; skipping verification"
    }

    # 4. extract to staging, then re-inject local config/state
    $stage = Join-Path $tmp "stage"
    Expand-Archive -Path $zipPath -DestinationPath $stage -Force
    foreach ($keep in @("config_local.json", "state.json", "update.log")) {
        $src = Join-Path $ExeDir $keep
        if (Test-Path $src) { Copy-Item $src (Join-Path $stage $keep) -Force }
    }

    # 5. backup current dir content, swap in staged files
    $backup = Join-Path $ExeDir "_backup_prev"
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    $files = Get-ChildItem $ExeDir -File | Where-Object { $_.Name -notin @("config_local.json", "state.json", "update.log", "version.txt") }
    $hadBackup = $false
    if (Stop-ServiceSafe) {
        $hadBackup = $true
        if (-not (Wait-CollectorExit)) { throw "collector process would not exit" }
        foreach ($f in $files) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            if (-not (Copy-WithRetry $f.FullName (Join-Path $backup $f.Name))) { throw "backup copy failed: $($f.Name)" }
        }
        foreach ($f in $files) {
            if (-not (Swap-File $stage $f.Name)) { throw "copy failed: $($f.Name)" }
        }
        Start-ServiceSafe
        Start-Sleep -Seconds 5
        if (-not (ServiceRunning)) { throw "service $ServiceName failed to start after update" }
    } else {
        # no service: the collector is likely running manually in a console —
        # waiting for / forcing exit is required or the exe swap fails
        if (-not (Wait-CollectorExit)) { throw "collector process would not exit (close the collector console and retry)" }
        foreach ($nf in Get-ChildItem $stage -File) {
            if (-not (Swap-File $stage $nf.Name)) { throw "copy failed: $($nf.Name)" }
        }
        Log "no service installed; start the collector manually now"
    }

    Set-Content -Path $VersionFile -Value $tag
    Log "updated to $tag OK"
} catch {
    Log "ERROR: $($_.Exception.Message)"
    if ($hadBackup) {
        Log "rolling back..."
        Stop-ServiceSafe | Out-Null
        Wait-CollectorExit | Out-Null
        foreach ($f in Get-ChildItem $backup -File) {
            try { Copy-Item $f.FullName (Join-Path $ExeDir $f.Name) -Force } catch { Log "WARNING: rollback copy failed: $($f.Name) - $($_.Exception.Message)" }
        }
        Start-ServiceSafe
        Log "rollback complete"
    }
    exit 1
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
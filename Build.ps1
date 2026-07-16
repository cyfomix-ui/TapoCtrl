param(
    [switch]$Publish,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'TapoCtrl\TapoCtrl.csproj'
$projectDir = Split-Path -Parent $project
$publishDir = Join-Path $projectDir 'bin\Release\net8.0-windows\win-x64\publish'

function Stop-TapoCtrlProcess {
    if ($KeepRunning) {
        return
    }

    $processes = @(Get-Process -Name 'TapoCtrl' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }

    Write-Host 'Stopping running TapoCtrl process before build...'
    $processes | Stop-Process -Force -ErrorAction Stop

    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $remaining = @(Get-Process -Name 'TapoCtrl' -ErrorAction SilentlyContinue)
    } while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)

    if ($remaining.Count -gt 0) {
        throw 'TapoCtrl.exe is still running. Close it from Task Manager and run Build.ps1 again.'
    }
}

function Remove-DirectoryWithRetry([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }
            Start-Sleep -Milliseconds (300 * $attempt)
        }
    }
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

if ($Publish) {
    Stop-TapoCtrlProcess

    # Single-file publish cannot overwrite an EXE that is still executing.
    Remove-DirectoryWithRetry (Join-Path $projectDir 'obj')
    Remove-DirectoryWithRetry (Join-Path $projectDir 'bin')

    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "Published: $publishDir"
}
else {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

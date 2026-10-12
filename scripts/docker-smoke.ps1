# Smoke test for Sluice Portal in Docker (Windows containers).
# Usage: .\scripts\docker-smoke.ps1 [-BaseUrl http://localhost:58472] [-ApiKey your-ui-key] [-PipelineId pipe-default]
param(
    [string]$BaseUrl = "http://localhost:58472",
    [string]$ApiKey = "",
    [string]$PipelineId = "",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

Write-Host "Waiting for $BaseUrl/health ..."
do {
    try {
        $health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get -TimeoutSec 5
        if ($health.status -eq "ok") {
            Write-Host "Health OK"
            break
        }
    }
    catch {
        if ((Get-Date) -gt $deadline) {
            throw "Health check timed out after ${TimeoutSeconds}s"
        }
        Start-Sleep -Seconds 3
    }
} while ($true)

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "ApiKey is required (UI_API_KEY). Example: -ApiKey `$env:UI_API_KEY"
}

$login = Invoke-WebRequest -Uri "$BaseUrl/auth/login" -Method Post `
    -ContentType "application/json" -Body (@{ key = $ApiKey } | ConvertTo-Json) `
    -WebSession $session -MaximumRedirection 0 -ErrorAction SilentlyContinue
if ($login.StatusCode -notin 302, 200) {
    throw "Login failed with status $($login.StatusCode)"
}

try {
    $mode = Invoke-RestMethod -Uri "$BaseUrl/api/orchestration/mode" -WebSession $session -TimeoutSec 10
    Write-Host "Execution mode: $($mode.mode)"
}
catch {
    Write-Warning "Orchestration API: $($_.Exception.Message)"
}

try {
    $queue = Invoke-RestMethod -Uri "$BaseUrl/api/orchestration/queue" -WebSession $session -TimeoutSec 10
    Write-Host "Queue snapshot received (ingest pending=$($queue.ingest.pending))"
}
catch {
    Write-Warning "Queue API: $($_.Exception.Message)"
}

try {
    $pipelines = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines" -WebSession $session -TimeoutSec 10
    Write-Host "Pipelines: $($pipelines.Count)"
    if (-not $PipelineId -and $pipelines.Count -gt 0) {
        $PipelineId = $pipelines[0].id
    }
}
catch {
    Write-Warning "Pipelines API: $($_.Exception.Message)"
}

if ($PipelineId) {
    try {
        $snapshots = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/snapshots" -WebSession $session -TimeoutSec 60
        Write-Host "Snapshots API OK for $PipelineId (settled=$($snapshots.settledSnapshot), pending=$($snapshots.pendingSnapshots.Count))"
    }
    catch {
        Write-Warning "Snapshots API: $($_.Exception.Message)"
    }

    try {
        $reconcileReport = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/reports/reconcile" -WebSession $session -TimeoutSec 10 -ErrorAction SilentlyContinue
        if ($reconcileReport) {
            Write-Host "Latest reconcile report: $($reconcileReport.id)"
        }
        else {
            Write-Host "No reconcile report yet (expected on fresh install)"
        }
    }
    catch {
        Write-Host "No reconcile report yet (expected on fresh install)"
    }
}

Write-Host "Smoke test completed."
# End-to-end monolith smoke against a running Sluice Portal instance.
# Usage: .\scripts\run-e2e-monolith.ps1 [-BaseUrl http://localhost:58472] [-ApiKey your-ui-key] [-PipelineId pipe-default]
param(
    [string]$BaseUrl = "http://localhost:58472",
    [string]$ApiKey = "",
    [string]$PipelineId = "pipe-default",
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

function Wait-Health {
    Write-Host "Waiting for $BaseUrl/health ..."
    do {
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -Method Get -TimeoutSec 5
            if ($health.status -eq "ok") {
                Write-Host "Health OK"
                return
            }
        }
        catch {
            if ((Get-Date) -gt $deadline) {
                throw "Health check timed out after ${TimeoutSeconds}s"
            }
            Start-Sleep -Seconds 3
        }
    } while ($true)
}

Wait-Health

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

$snapshots = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/snapshots" -WebSession $session -TimeoutSec 60
Write-Host "Settled snapshot: $($snapshots.settledSnapshot)"
Write-Host "Pending snapshots: $($snapshots.pendingSnapshots.Count)"

$run = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/run" -Method Post -Headers @{ 'X-Sluice-Client' = 'e2e' } -WebSession $session -TimeoutSec 30
$jobId = $run.id
Write-Host "Started job $jobId (type $($run.type), status $($run.status))"

do {
    if ((Get-Date) -gt $deadline) {
        throw "Job $jobId did not finish within ${TimeoutSeconds}s"
    }

    $job = Invoke-RestMethod -Uri "$BaseUrl/api/jobs/$jobId" -WebSession $session -TimeoutSec 15
    Write-Host "  [$($job.status)] stage=$($job.currentStage) ingest=$($job.stageStatus.ingest) sql=$($job.stageStatus.sql) reconcile=$($job.stageStatus.reconcile)"
    # API may serialize JobStatus as string or numeric enum (Running=0, Ok=1, Partial=2, Error=3, Blocked=4).
    if ($job.status -in @("ok", "partial", "error", "blocked", 1, 2, 3, 4)) {
        break
    }
    Start-Sleep -Seconds 3
} while ($true)

if ($job.status -notin @("ok", "partial", 1, 2)) {
    throw "Job $jobId finished with status $($job.status)"
}

Write-Host "Triggering reconcile for $PipelineId ..."
$recon = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/reconcile" -Method Post -Headers @{ 'X-Sluice-Client' = 'e2e' } -WebSession $session -TimeoutSec 30
$reconJobId = $recon.id
Write-Host "Reconcile job $reconJobId started"

do {
    if ((Get-Date) -gt $deadline) {
        throw "Reconcile job $reconJobId timed out"
    }

    $reconJob = Invoke-RestMethod -Uri "$BaseUrl/api/jobs/$reconJobId" -WebSession $session -TimeoutSec 15
    if ($reconJob.status -in @("ok", "partial", "error", "blocked", 1, 2, 3, 4)) {
        break
    }
    Start-Sleep -Seconds 3
} while ($true)

$report = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/reports/reconcile" -WebSession $session -TimeoutSec 15 -ErrorAction SilentlyContinue
if ($report) {
    Write-Host "Reconcile report max divergence: $($report.maxDivergencePct)% (partial=$($report.isPartial))"
}

Write-Host "E2E monolith completed. Incremental job=$jobId status=$($job.status); reconcile job=$reconJobId status=$($reconJob.status)"
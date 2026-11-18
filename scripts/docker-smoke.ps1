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
        # 200 con "degraded": el portal arrancó, pero el scheduler o el disco piden atención.
        if ($health.status -eq "degraded") {
            Write-Warning "Health degraded (scheduler=$($health.scheduler), disk=$($health.disk))"
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
# Cada llamada obligatoria que falla se anota aquí y el smoke sigue con las demás; al final, si hay
# alguna, termina con código 1 y el resumen.
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$Call, $ErrorRecord) {
    $response = $ErrorRecord.Exception.Response
    $detail = if ($response) { "HTTP $([int]$response.StatusCode)" } else { $ErrorRecord.Exception.Message }
    $failures.Add("${Call}: $detail")
    Write-Warning "${Call}: $detail"
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "ApiKey is required (UI_API_KEY). Example: -ApiKey `$env:UI_API_KEY"
}

# Fuera de Development la cookie de sesión es Secure y PowerShell no la devuelve por http (los
# navegadores sí, en localhost): se copia sin Secure en la sesión para que las llamadas la lleven.
try {
    Invoke-WebRequest -Uri "$BaseUrl/auth/login" -Method Post -UseBasicParsing `
        -ContentType "application/json" -Body (@{ key = $ApiKey } | ConvertTo-Json) `
        -WebSession $session | Out-Null
    $cookie = $session.Cookies.GetCookies([Uri]($BaseUrl -replace '^http:', 'https:'))["sluice_session"]
    if ($cookie) {
        $session.Cookies.Add([Uri]$BaseUrl, (New-Object System.Net.Cookie "sluice_session", $cookie.Value, "/"))
        Write-Host "Login OK"
    }
    else {
        $failures.Add("Login: no session cookie in the response")
        Write-Warning "Login: no session cookie in the response"
    }
}
catch {
    Add-Failure "Login" $_
}

try {
    $mode = Invoke-RestMethod -Uri "$BaseUrl/api/orchestration/mode" -WebSession $session -TimeoutSec 10
    Write-Host "Execution mode: $($mode.mode)"
}
catch {
    Add-Failure "Orchestration API" $_
}

try {
    $queue = Invoke-RestMethod -Uri "$BaseUrl/api/orchestration/queue" -WebSession $session -TimeoutSec 10
    Write-Host "Queue snapshot received (ingest pending=$($queue.ingest.pending))"
}
catch {
    Add-Failure "Queue API" $_
}

try {
    $pipelines = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines" -WebSession $session -TimeoutSec 10
    Write-Host "Pipelines: $($pipelines.Count)"
    if (-not $PipelineId -and $pipelines.Count -gt 0) {
        $PipelineId = $pipelines[0].id
    }
}
catch {
    Add-Failure "Pipelines API" $_
}

if ($PipelineId) {
    try {
        $snapshots = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/snapshots" -WebSession $session -TimeoutSec 60
        Write-Host "Snapshots API OK for $PipelineId (settled=$($snapshots.settledSnapshot), pending=$($snapshots.pendingSnapshots.Count))"
    }
    catch {
        Add-Failure "Snapshots API" $_
    }

    try {
        $reconcileReport = Invoke-RestMethod -Uri "$BaseUrl/api/pipelines/$PipelineId/reports/reconcile" -WebSession $session -TimeoutSec 10
        if ($reconcileReport) {
            Write-Host "Latest reconcile report: $($reconcileReport.id)"
        }
        else {
            Write-Host "No reconcile report yet (expected on fresh install)"
        }
    }
    catch {
        # Solo el 404 es normal (aún no hay reporte); cualquier otro error cuenta como fallo.
        $response = $_.Exception.Response
        if ($response -and [int]$response.StatusCode -eq 404) {
            Write-Host "No reconcile report yet (expected on fresh install)"
        }
        else {
            Add-Failure "Reconcile report API" $_
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Smoke test FAILED ($($failures.Count) call(s)):"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host "Smoke test completed."
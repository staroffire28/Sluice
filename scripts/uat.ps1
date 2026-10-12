# Suite UAT por interfaz (Playwright), bajo demanda.
#
#   pwsh scripts/uat.ps1                          # todo; levanta un SQL Server efímero en Docker
#   pwsh scripts/uat.ps1 -Area Pipelines          # un área (Access, Sources, Destinations, Pipelines,
#                                                 #   Jobs, Details, Quality, Navigation, Settings, Variants)
#   pwsh scripts/uat.ps1 -Scenario UAT-PIP-02     # un escenario ("contiene": UAT-PIP-0 ejecuta del 01 al 09)
#   pwsh scripts/uat.ps1 -NoSql                   # solo los escenarios que no necesitan SQL
#   pwsh scripts/uat.ps1 -Headed                  # con el navegador visible
#
# SQL: usa $env:SLUICE_UAT_SQL (connection string a master) si está definida; si no, arranca un
# contenedor mssql efímero (contraseña aleatoria, 2 GB de memoria como máximo) y lo borra al
# terminar. La suite crea su propia base SluiceUat_<id> y la elimina. Nunca toca el storage del
# cliente: el origen son carpetas locales generadas por la propia suite.
#
# Resultado: TestResults/uat/uat-report.html (+ .trx). Si un escenario falla, su carpeta
# TestResults/uat/<ID>/ guarda la traza (npx playwright show-trace trace.zip), captura y vídeo.
param(
    [string]$Area = "",
    [string]$Scenario = "",
    [switch]$NoSql,
    [switch]$Headed,
    [switch]$NoBuild,
    [switch]$OpenReport
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root "TestResults/uat"
$project = Join-Path $root "Sluice.Portal.UatTests"
$container = $null

function Start-EphemeralSql {
    $password = "Uat_" + [guid]::NewGuid().ToString("N").Substring(0, 20) + "!a1"
    $name = "sluice-uat-sql-" + [guid]::NewGuid().ToString("N").Substring(0, 6)
    Write-Host "Arrancando SQL Server efímero ($name)..."
    docker run -d --name $name `
        -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" -e MSSQL_MEMORY_LIMIT_MB=2048 `
        --memory 2g -p 127.0.0.1::1433 mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo arrancar el contenedor de SQL Server." }

    $port = ((docker port $name 1433) | Select-Object -First 1).Split(":")[-1]
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $password -Q "SELECT 1" -b 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "SQL Server listo en 127.0.0.1,$port"
            return @{
                Name = $name
                ConnectionString = "Server=127.0.0.1,$port;Database=master;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
            }
        }
        Start-Sleep -Seconds 2
    }

    docker rm -f $name | Out-Null
    throw "SQL Server no estuvo listo en 120 s."
}

# Lanzado como .\scripts\uat.ps1 corre en el proceso de la consola: las variables que fija abajo se
# restauran al terminar, para que la siguiente ejecución no use el SQL efímero ya borrado.
$savedEnv = @{}
foreach ($name in "SLUICE_UAT_SQL", "UAT_ARTIFACTS", "UAT_HEADED") {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    if (-not $NoBuild) {
        dotnet build (Join-Path $root "Sluice.Portal.sln") -c Debug
        if ($LASTEXITCODE -ne 0) { throw "La compilación falló." }
        pwsh (Join-Path $project "bin/Debug/net10.0/playwright.ps1") install chromium
        if ($LASTEXITCODE -ne 0) { throw "No se pudo instalar Chromium para Playwright." }
    }

    if ($NoSql) {
        $env:SLUICE_UAT_SQL = ""
    }
    elseif ([string]::IsNullOrWhiteSpace($env:SLUICE_UAT_SQL)) {
        $sql = Start-EphemeralSql
        $container = $sql.Name
        $env:SLUICE_UAT_SQL = $sql.ConnectionString
    }

    $env:UAT_ARTIFACTS = $results
    $env:UAT_HEADED = if ($Headed) { "1" } else { "" }

    $filters = @()
    if ($Area) { $filters += "Area=$Area" }
    if ($Scenario) { $filters += "DisplayName~$Scenario" }

    $testArgs = @(
        "test", $project, "--no-build",
        "--results-directory", $results,
        "--logger", "html;LogFileName=uat-report.html",
        "--logger", "trx;LogFileName=uat.trx",
        "--logger", "console;verbosity=normal"
    )
    if ($filters.Count -gt 0) { $testArgs += @("--filter", ($filters -join "&")) }

    dotnet @testArgs
    $exitCode = $LASTEXITCODE

    $report = Join-Path $results "uat-report.html"
    Write-Host ""
    Write-Host "Informe: $report"
    if ($OpenReport -and (Test-Path $report)) { Invoke-Item $report }
    exit $exitCode
}
finally {
    foreach ($entry in $savedEnv.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }

    if ($container) {
        Write-Host "Borrando el SQL Server efímero ($container)..."
        docker rm -f $container | Out-Null
    }
}

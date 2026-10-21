# Restaura un backup de pipeline-data (ver scripts\backup-data-dir.ps1) DENTRO de un
# volumen Docker existente. SOBRESCRIBE el contenido actual del volumen -- confirma
# antes de continuar. Detén api/worker-* antes de restaurar (el rename atómico de la
# cola no espera a que el volumen cambie por debajo).
#
# Sin -VolumeName, busca el volumen por la etiqueta que le pone Compose
# (com.docker.compose.volume=pipeline-data), sea cual sea el nombre del proyecto.
#
# Uso: .\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-20260701120000.tar.gz [-VolumeName sluice_pipeline-data]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,
    [string]$VolumeName
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BackupFile)) {
    throw "No se encontró el archivo de backup: $BackupFile"
}

if (-not $VolumeName) {
    $candidates = @(docker volume ls --quiet --filter "label=com.docker.compose.volume=pipeline-data")
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron listar los volúmenes (docker volume ls exit code $LASTEXITCODE)."
    }
    if ($candidates.Count -eq 0) {
        throw "No hay ningún volumen 'pipeline-data' creado por Compose. Créalo primero (p.ej. con 'docker compose create') o indica el volumen con -VolumeName."
    }
    if ($candidates.Count -gt 1) {
        throw "Hay varios volúmenes 'pipeline-data' creados por Compose: $($candidates -join ', '). Indica cuál con -VolumeName."
    }
    $VolumeName = $candidates[0]
}

$volumeExists = docker volume inspect $VolumeName 2>$null
if (-not $volumeExists) {
    throw "El volumen '$VolumeName' no existe. Créalo primero (p.ej. levantando el compose una vez) antes de restaurar."
}

Write-Host "ADVERTENCIA: esto reemplaza TODO el contenido actual del volumen '$VolumeName' con $BackupFile."
$confirmation = Read-Host "Escribe 'restore' para confirmar"
if ($confirmation -ne "restore") {
    Write-Host "Cancelado."
    exit 1
}

$backupFileFull = (Resolve-Path $BackupFile).Path
$backupDir = Split-Path $backupFileFull -Parent
$backupName = Split-Path $backupFileFull -Leaf

Write-Host "Restaurando $BackupFile -> volumen '$VolumeName' ..."
docker run --rm `
    -v "${VolumeName}:/data" `
    -v "${backupDir}:/backup:ro" `
    alpine `
    sh -c "rm -rf /data/* /data/.[!.]* 2>/dev/null; tar xzf /backup/$backupName -C /data"

if ($LASTEXITCODE -ne 0) {
    throw "La restauración falló (docker run exit code $LASTEXITCODE)."
}

Write-Host "Restauración completada. Levanta el portal y verifica /health y que las conexiones existentes descifran correctamente (el keyring de Data Protection viajó junto con los secretos)."

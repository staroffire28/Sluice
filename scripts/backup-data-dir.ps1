# Backup del volumen Docker pipeline-data (DATA_DIR compartido entre api/worker-*).
# Incluye conexiones (secretos cifrados at-rest), pipelines, jobs, cola de tareas,
# reportes y el keyring de Data Protection que descifra los secretos de conexión --
# perder el keyring vuelve irrecuperables las credenciales ADLS/SQL ya guardadas
# aunque el resto del volumen sobreviva.
#
# Requiere Docker Engine con contenedores Linux (usa la imagen "alpine" para el tar;
# funciona igual con Docker Desktop en modo Linux containers / WSL2, que es el
# despliegue documentado en docker-compose.linux.yml). Para Windows containers,
# adapta el contenedor helper (p.ej. mcr.microsoft.com/windows/nanoserver + Compress-Archive).
#
# Sin -VolumeName, busca el volumen por la etiqueta que le pone Compose
# (com.docker.compose.volume=pipeline-data), sea cual sea el nombre del proyecto.
#
# Uso: .\scripts\backup-data-dir.ps1 [-VolumeName sluice_pipeline-data] [-OutputDir .\backups]
param(
    [string]$VolumeName,
    [string]$OutputDir = ".\backups"
)

$ErrorActionPreference = "Stop"

if (-not $VolumeName) {
    $candidates = @(docker volume ls --quiet --filter "label=com.docker.compose.volume=pipeline-data")
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron listar los volúmenes (docker volume ls exit code $LASTEXITCODE)."
    }
    if ($candidates.Count -eq 0) {
        throw "No hay ningún volumen 'pipeline-data' creado por Compose. Indica el volumen con -VolumeName (docker volume ls)."
    }
    if ($candidates.Count -gt 1) {
        throw "Hay varios volúmenes 'pipeline-data' creados por Compose: $($candidates -join ', '). Indica cuál con -VolumeName."
    }
    $VolumeName = $candidates[0]
}

$volumeExists = docker volume inspect $VolumeName 2>$null
if (-not $volumeExists) {
    throw "El volumen '$VolumeName' no existe. Verifica el nombre (docker volume ls)."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$outputDirFull = (Resolve-Path $OutputDir).Path
$timestamp = Get-Date -Format "yyyyMMddHHmmss"
$archiveName = "pipeline-data-$timestamp.tar.gz"

Write-Host "Empaquetando volumen '$VolumeName' -> $OutputDir\$archiveName ..."
docker run --rm `
    -v "${VolumeName}:/data:ro" `
    -v "${outputDirFull}:/backup" `
    alpine `
    tar czf "/backup/$archiveName" -C /data .

if ($LASTEXITCODE -ne 0) {
    throw "El backup falló (docker run exit code $LASTEXITCODE)."
}

$archivePath = Join-Path $outputDirFull $archiveName
$sizeMb = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
Write-Host "Backup completado: $archivePath ($sizeMb MB)"
Write-Host "Recuerda probar la restauración periódicamente (scripts\restore-data-dir.ps1) — un backup nunca probado no cuenta como backup."

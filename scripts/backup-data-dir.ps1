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
# El backup se cifra con age (https://age-encryption.org) para la clave pública -Recipient;
# para restaurarlo hace falta la clave privada (ver docs\BACKUP.md). Sin cifrar, solo con
# -NoEncrypt explícito: el tar lleva los secretos y el keyring que los descifra.
#
# Uso: .\scripts\backup-data-dir.ps1 -Recipient age1... [-VolumeName sluice_pipeline-data] [-OutputDir .\backups]
#      .\scripts\backup-data-dir.ps1 -NoEncrypt [...]
param(
    [string]$VolumeName,
    [string]$OutputDir = ".\backups",
    [string]$Recipient,
    [switch]$NoEncrypt
)

$ErrorActionPreference = "Stop"

if ($Recipient -and $NoEncrypt) {
    throw "Usa -Recipient o -NoEncrypt, no los dos."
}
if (-not $Recipient -and -not $NoEncrypt) {
    throw "Indica la clave pública de age con -Recipient (age1...). El backup lleva los secretos de conexión y el keyring que los descifra; para guardarlo sin cifrar, pasa -NoEncrypt."
}
if ($Recipient -and -not (Get-Command age -ErrorAction SilentlyContinue)) {
    throw "No se encuentra 'age' en el PATH. Instálalo (https://age-encryption.org) antes de hacer el backup."
}

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
if ($Recipient) {
    Write-Host "Cifrando con age -> $archiveName.age ..."
    try {
        age -r $Recipient -o "$archivePath.age" $archivePath
        if ($LASTEXITCODE -ne 0) {
            throw "El cifrado falló (age exit code $LASTEXITCODE)."
        }
    } finally {
        # El tar sin cifrar no se queda en disco, ni siquiera si age falla.
        Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
    }
    $archivePath = "$archivePath.age"
}
$sizeMb = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
Write-Host "Backup completado: $archivePath ($sizeMb MB)"
Write-Host "Recuerda probar la restauración periódicamente (scripts\restore-data-dir.ps1) — un backup nunca probado no cuenta como backup."

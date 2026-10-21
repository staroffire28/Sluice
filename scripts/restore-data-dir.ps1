# Restaura un backup de pipeline-data (ver scripts\backup-data-dir.ps1) DENTRO de un
# volumen Docker existente. SOBRESCRIBE el contenido actual del volumen -- confirma
# antes de continuar. Detén api/worker-* antes de restaurar (el rename atómico de la
# cola no espera a que el volumen cambie por debajo).
#
# Sin -VolumeName, busca el volumen por la etiqueta que le pone Compose
# (com.docker.compose.volume=pipeline-data), sea cual sea el nombre del proyecto.
#
# Un backup cifrado (.age) se descifra con la clave privada -Identity. Antes de tocar el
# volumen, el script descifra el backup y lista el tar entero: si algo falla, no borra nada.
#
# Uso: .\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-20260701120000.tar.gz.age -Identity .\sluice-backup.key [-VolumeName sluice_pipeline-data]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,
    [string]$VolumeName,
    [string]$Identity
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

$backupFileFull = (Resolve-Path $BackupFile).Path
$tempDir = $null
if ($backupFileFull.EndsWith(".age")) {
    if (-not $Identity) {
        throw "El backup está cifrado: indica la clave privada de age con -Identity."
    }
    if (-not (Get-Command age -ErrorAction SilentlyContinue)) {
        throw "No se encuentra 'age' en el PATH. Instálalo (https://age-encryption.org) antes de restaurar."
    }
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "sluice-restore-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    if ($IsWindows -eq $false) {
        chmod 700 $tempDir
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo restringir la carpeta temporal $tempDir (chmod exit code $LASTEXITCODE)."
        }
    }
}

try {
    if ($tempDir) {
        Write-Host "Descifrando $BackupFile ..."
        $tarFull = Join-Path $tempDir "pipeline-data.tar.gz"
        age -d -i $Identity -o $tarFull $backupFileFull
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo descifrar el backup (age exit code $LASTEXITCODE): la clave privada no es la del backup o el fichero está dañado. No se ha tocado el volumen."
        }
    } else {
        $tarFull = $backupFileFull
    }
    $backupDir = Split-Path $tarFull -Parent
    $backupName = Split-Path $tarFull -Leaf

    Write-Host "Comprobando el tar ..."
    docker run --rm -v "${backupDir}:/backup:ro" alpine tar tzf "/backup/$backupName" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "El backup no es un tar.gz válido (docker run exit code $LASTEXITCODE). No se ha tocado el volumen."
    }

    Write-Host "ADVERTENCIA: esto reemplaza TODO el contenido actual del volumen '$VolumeName' con $BackupFile."
    $confirmation = Read-Host "Escribe 'restore' para confirmar"
    if ($confirmation -ne "restore") {
        Write-Host "Cancelado."
        exit 1
    }

    Write-Host "Restaurando $BackupFile -> volumen '$VolumeName' ..."
    docker run --rm `
        -v "${VolumeName}:/data" `
        -v "${backupDir}:/backup:ro" `
        alpine `
        sh -c "rm -rf /data/* /data/.[!.]* 2>/dev/null; tar xzf /backup/$backupName -C /data"

    if ($LASTEXITCODE -ne 0) {
        throw "La restauración falló (docker run exit code $LASTEXITCODE)."
    }
} finally {
    # El tar descifrado no se queda en disco.
    if ($tempDir) {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}

Write-Host "Restauración completada. Levanta el portal y verifica /health y que las conexiones existentes descifran correctamente (el keyring de Data Protection viajó junto con los secretos)."

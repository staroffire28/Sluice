# Reconstruye el container monolito con el código actual PRESERVANDO las
# credenciales del container en ejecución (o el último parado).
# Las env se leen en runtime desde el container actual; no se escriben a disco.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

Remove-Item -ErrorAction SilentlyContinue 'scripts\redeploy-done.txt'

# Azure/SQL ya no van por env (se configuran por conexión en el portal) y los
# operativos viven en /settings. Solo se preserva la clave de la UI.
$keep = @('UI_API_KEY')
$envs = docker inspect sluice-portal-1 --format '{{range .Config.Env}}{{println .}}{{end}}'
foreach ($line in $envs) {
    foreach ($key in $keep) {
        if ($line -match "^($key)=(.*)$") {
            Set-Item -Path "env:$($matches[1])" -Value $matches[2]
        }
    }
}

# docker compose escribe el progreso del build por stderr. Bajo ErrorActionPreference
# 'Stop', PowerShell trata ese stderr como NativeCommandError y aborta aunque el build
# vaya bien (falso negativo). Lo ejecutamos con manejo relajado y validamos por el
# código de salida real ($LASTEXITCODE).
$ErrorActionPreference = 'Continue'
docker compose -f docker-compose.monolith.yml up -d --build 2>&1 |
    Tee-Object -FilePath 'scripts\redeploy.log'
$exit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

"EXIT=$exit" | Out-File -FilePath 'scripts\redeploy-done.txt' -Encoding ascii

if ($exit -ne 0) {
    Write-Error "docker compose falló (EXIT=$exit). Ver scripts\redeploy.log"
    exit $exit
}

Write-Host "Redeploy OK (EXIT=0)"

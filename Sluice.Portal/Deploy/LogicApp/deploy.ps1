# Despliega la Logic App de alertas de Sluice (azuredeploy.json) con Azure CLI.
#
#   ./deploy.ps1 -ResourceGroup rg-sluice -Location westeurope `
#     -Recipients "operaciones@contoso.com;dba@contoso.com" -PortalUrl "https://sluice.contoso.com"
#
# Requiere `az login`. Con -Location crea el grupo de recursos si no existe.
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $Recipients,
    [string] $PortalUrl = "",
    [string] $Location = "",
    [string] $Name = "sluice-alertas"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "No se encontró Azure CLI (az). Instálalo o despliega desde el Azure Portal (ver README.md)."
}

if ($Location) {
    az group create --name $ResourceGroup --location $Location --output none
    if ($LASTEXITCODE -ne 0) { throw "No se pudo crear o leer el grupo de recursos $ResourceGroup." }
}

$template = Join-Path $PSScriptRoot "azuredeploy.json"
$outputs = az deployment group create `
    --resource-group $ResourceGroup `
    --name "sluice-alertas-$(Get-Date -Format yyyyMMddHHmmss)" `
    --template-file $template `
    --parameters logicAppName=$Name recipients=$Recipients portalBaseUrl=$PortalUrl `
    --query properties.outputs --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "El despliegue falló (ver el error de az arriba)." }

Write-Host ""
Write-Host "Logic App '$($outputs.logicAppName.value)' desplegada en $ResourceGroup." -ForegroundColor Green
Write-Host ""
Write-Host "Pasos que faltan:" -ForegroundColor Cyan
Write-Host "  1. Autoriza la conexión de Office 365: Azure Portal > $ResourceGroup > conexión de API"
Write-Host "     '$($outputs.connectionName.value)' > Editar conexión de API > Autorizar > Guardar."
Write-Host "  2. En Sluice > Ajustes > Alertas: activa las alertas, pega esta URL, Guardar y Probar alerta."
Write-Host "     (Es un secreto: no la compartas.)"
Write-Host ""
Write-Host $outputs.triggerUrl.value

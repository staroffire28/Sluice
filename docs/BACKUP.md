# Backup y recuperación de DATA_DIR

## Por qué

Sluice Portal no usa base de datos: todo el estado operativo (conexiones con secretos
cifrados at-rest, pipelines, jobs, cola de tareas, reportes de reconcile/diff, y el
keyring de ASP.NET Data Protection que descifra los secretos de conexión) vive en el
volumen Docker `pipeline-data` (`DATA_DIR`). Es un único volumen sin réplica: perderlo o
corromperlo implica perder todo el estado del portal, y perder el keyring en particular
vuelve **irrecuperables** las credenciales ADLS/SQL ya guardadas aunque el resto del
volumen sobreviva intacto.

## Qué incluye el backup

Todo `DATA_DIR`: `connections/`, `pipelines/`, `jobs/`, `tasks/`, `reports/`, `keys/`
(keyring de Data Protection), `workers/`, el estado por pipeline (`{pipelineId}/state/`) y
`parquet/` (el parquet consolidado de los orígenes que lo guardan en el disco del portal).
No incluye el parquet consolidado guardado en ADLS ni los snapshots de ADLS (eso vive en
Azure, fuera del volumen local, y tiene su propia durabilidad/replicación gestionada por
Azure Storage).

## Cómo hacer un backup

```powershell
.\scripts\backup-data-dir.ps1
# o con parámetros explícitos:
.\scripts\backup-data-dir.ps1 -VolumeName pipeline-data -OutputDir .\backups
```

Genera `backups\pipeline-data-<timestamp>.tar.gz`. Guarda esta copia **fuera** del host
(otro disco, blob storage, etc.) — un backup en el mismo disco que el volumen original no
protege contra un fallo de disco.

Recomendación operativa: automatizar esta llamada con el Programador de tareas de Windows
o un cron equivalente (p.ej. diario), rotando/purgando backups antiguos según tu política
de retención.

## Cómo restaurar

```powershell
# 1. Detén el portal y los workers (evita escribir mientras se restaura):
docker compose -f docker-compose.linux.yml down

# 2. Restaura (sobrescribe TODO el contenido actual del volumen, pide confirmación):
.\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-20260701120000.tar.gz

# 3. Levanta de nuevo y verifica:
docker compose -f docker-compose.linux.yml up -d
curl http://localhost:58472/health
curl http://localhost:58472/health/ready   # valida además conectividad ADLS/SQL real
```

Tras restaurar, confirma en el portal (Sources/Destinations → "Test de conexión") que las
conexiones existentes descifran correctamente — el keyring de Data Protection viaja junto
con los secretos en el mismo backup, así que un restore consistente (mismo backup para
ambos) siempre debería descifrar bien.

## RTO/RPO (orientativo, ajustar según tu operación real)

- **RPO** (cuánto se puede perder): igual a la frecuencia del backup. Con backup diario,
  hasta 24h de jobs/cambios de configuración en el peor caso.
- **RTO** (cuánto tarda recuperar): minutos — el script solo empaqueta/desempaqueta el
  volumen; el tiempo dominante es levantar los contenedores de nuevo.

## Importante

**Prueba la restauración periódicamente en un volumen de prueba.** Un backup que nunca se
restauró no es un backup verificado — puede estar corrupto, incompleto, o el script puede
haber dejado de funcionar por un cambio en el entorno sin que nadie lo note hasta que haga
falta de verdad.

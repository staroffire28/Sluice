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
`parquet/` (el parquet consolidado de los orígenes que lo guardan en el disco del portal, con
sus versiones anteriores en `parquet/_versions/`).
No incluye el parquet consolidado guardado en ADLS ni los snapshots de ADLS (eso vive en
Azure, fuera del volumen local, y tiene su propia durabilidad/replicación gestionada por
Azure Storage).

## La clave de cifrado

El backup lleva los secretos de conexión **y** el keyring que los descifra, así que se
guarda cifrado con [age](https://age-encryption.org) (`winget install FiloSottile.age`,
`brew install age` o el paquete de tu distribución). Genera el par de claves una vez:

```powershell
age-keygen -o sluice-backup.key
# Public key: age1...   <- la clave pública, para -Recipient
```

- La **clave pública** (`age1...`) no es secreta: puede quedarse en el host y en la tarea
  programada que haga los backups.
- La **clave privada** (`sluice-backup.key`) es la única forma de restaurar. Guárdala
  **fuera** del host y lejos de los backups: en un gestor de contraseñas o en una caja fuerte
  de secretos, con una segunda copia en otro sitio. Quien tenga el backup y la clave privada
  tiene las credenciales ADLS/SQL; quien pierda la clave privada pierde todos los backups.
  Borra `sluice-backup.key` del host en cuanto la hayas guardado.

## Cómo hacer un backup

Para el stack antes del backup: con los contenedores escribiendo (cola de tareas, jobs,
estado), el tar puede quedar a medias e inconsistente.

```powershell
# 1. Para el portal y los workers (los contenedores se conservan):
docker compose -f docker-compose.linux.yml stop

# 2. Backup cifrado para tu clave pública:
.\scripts\backup-data-dir.ps1 -Recipient age1...
# o con parámetros explícitos:
.\scripts\backup-data-dir.ps1 -Recipient age1... -VolumeName sluice_pipeline-data -OutputDir .\backups

# 3. Vuelve a arrancarlos:
docker compose -f docker-compose.linux.yml start
```

Sin `-Recipient` el script falla. Para un backup sin cifrar (por ejemplo, para mover los
datos a otro volumen en el mismo host), pasa `-NoEncrypt` y borra el tar en cuanto acabes.

Sin `-VolumeName`, los scripts de backup y restore buscan el volumen por la etiqueta que le
pone Compose (`com.docker.compose.volume=pipeline-data`), sea cual sea el nombre del
proyecto. Si no encuentran ninguno, o encuentran varios (por ejemplo, el de un despliegue
anterior desde otra carpeta), fallan sin tocar nada y piden que indiques cuál con
`-VolumeName`.

Genera `backups\pipeline-data-<timestamp>.tar.gz.age` (el tar sin cifrar no se queda en
disco). Guarda esta copia **fuera** del host (otro disco, blob storage, etc.) — un backup en
el mismo disco que el volumen original no protege contra un fallo de disco.

Recomendación operativa: automatizar los tres pasos (parar, backup y arrancar) con el
Programador de tareas de Windows o un cron equivalente (p.ej. diario, en una hora sin
cargas programadas), rotando/purgando backups antiguos según tu política de retención.

## Cómo restaurar

```powershell
# 1. Detén el portal y los workers (evita escribir mientras se restaura):
docker compose -f docker-compose.linux.yml down

# 2. Restaura con la clave privada (sobrescribe TODO el contenido actual del volumen, pide confirmación):
.\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-20260701120000.tar.gz.age -Identity .\sluice-backup.key

# 3. Levanta de nuevo y verifica:
docker compose -f docker-compose.linux.yml up -d
curl http://localhost:58472/health
curl http://localhost:58472/health/ready   # valida además conectividad ADLS/SQL real
```

Antes de tocar el volumen, el script descifra el backup y lista el tar entero: si la clave
privada no es la buena o el backup está dañado, falla sin borrar nada. El tar descifrado se
borra al terminar.

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

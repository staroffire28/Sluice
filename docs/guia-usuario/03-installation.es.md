# Instalación

El portal es una sola app .NET 10. La misma imagen corre como orquestador (`api`, sirve la UI y agenda) o como worker por etapa (`ingest`, `sql`, `reconcile`); el rol se define en tiempo de ejecución. Todo el estado vive como archivos JSON en un volumen nombrado: el portal no tiene base de datos propia.

Hay dos variantes de contenedor; elige la que corre tu host:

- **Linux containers** — `Dockerfile.linux` + `docker-compose.linux.yml`.
- **Windows containers** — `Dockerfile` + `docker-compose.yml`.

## Requisitos de hardware

El portal es liviano; los sistemas pesados (SQL Server, ADLS) son externos. Estas cifras son para el host que corre los contenedores.

| Perfil | vCPU | RAM | Disco |
|---|---|---|---|
| Mínimo (PoC / bajo volumen) | 2 | 4 GB | 20 GB |
| Recomendado (producción) | 4–8 | 16 GB | 100 GB+ SSD |

- El **disco** lo domina el volumen `pipeline-data` (jobs, reportes, staging de parquet). Está acotado por la retención (90 días por defecto), salvo la copia local de cada Parquet consolidado guardado en ADLS, que ocupa lo mismo que esos ficheros (ver *Volumen local* en Retención de la referencia). Usa SSD para la cola.
- La **RAM** tiene su pico en la consolidación de parquet (un snapshot a la vez). 16 GB es cómodo para varios pipelines en paralelo.
- SQL Server y ADLS corren en otra parte y no se cuentan aquí.

## Prerrequisitos

- **Docker Engine 24+** con Docker Compose v2.
- **Host Linux**: cualquier distro con Docker (Linux containers, el modo por defecto del daemon).
- **Host Windows**: Docker Desktop en modo **Windows containers** (Windows Server 2022 o Windows 11 + Hyper-V).
- Un valor para `UI_API_KEY` en `.env` (o `AUTH_MODE=none` si quieres el acceso abierto, sin clave; ver Variables de entorno).

Las credenciales de Azure/SQL **no** se ponen aquí: se configuran en el portal después de arrancar.

## Pruébalo: modo demo

Para ver Sluice funcionando sin un entorno de F&O, sin cuenta de almacenamiento de Azure y sin tu propio SQL Server, ejecuta la demo en un equipo con Docker (Linux containers):

```bash
docker compose -f docker-compose.demo.yml up --build
```

Después abre `http://localhost:58480` y pulsa **Entrar**. La demo:

- Arranca su propio SQL Server (Developer Edition; al ejecutarla aceptas su licencia) y crea la base `SluiceDemo`.
- Escribe datos **sintéticos** de Synapse Link para tres tablas de F&O (`custtable`, `vendtable`, `generaljournalaccountentry`), con metadatos de enums.
- Crea el origen, el destino y el pipeline **Demo F&O**, y lanza la primera carga.
- Cada 5 minutos cierra un snapshot nuevo con altas, cambios y borrados; el scheduler lo procesa, así que puedes ver el Incremental, los borrados y la conciliación. Entre snapshots el scheduler también puede lanzar un Incremental sin nada nuevo; es lo esperado.

La cabecera muestra **Demo** y **Acceso abierto**. **No es para producción**: sin clave, con una contraseña de SQL fija salvo que definas `DEMO_SQL_PASSWORD` y con datos de prueba. El portal solo se publica en este equipo (`127.0.0.1`) y SQL Server solo para el portal. Para quitarla: `docker compose -f docker-compose.demo.yml down -v`. Cambia el puerto con `DEMO_HTTP_PORT` y la cadencia con `DEMO_SNAPSHOT_MINUTES`.

## Instalación (distribuido)

El modo distribuido corre un orquestador (`api`) más tres workers (`ingest`, `sql`, `reconcile`), todos compartiendo el volumen nombrado `pipeline-data`.

**1. Obtén el código y fija la clave:**

```bash
git clone https://github.com/staroffire28/Sluice.git
cd Sluice
cp .env.example .env          # edita UI_API_KEY; opcional SLUICE_HTTP_PORT (def. 58472)
```

**2. Construye y arranca — Linux:**

```bash
docker compose -f docker-compose.linux.yml up -d --build
```

**2. Construye y arranca — Windows:**

```powershell
docker compose -f docker-compose.yml up -d --build
```

**3. Verifica:**

```bash
curl http://localhost:58472/health      # → {"status":"ok"}
```

Luego abre `http://localhost:58472/login`, inicia sesión con tu `UI_API_KEY` (con `AUTH_MODE=none`, pulsa **Entrar**) y confirma que la pastilla de salud diga **Online**.

**Actualizar a una versión nueva:** tras `git pull`, repite el comando del paso 2. Reconstruye la imagen y recrea los contenedores que cambian; el volumen (datos, cola y keyring) se conserva y `UI_API_KEY` sigue saliendo de `.env`.

> Se requiere un **volumen nombrado de Docker** (la cola de tareas usa renombrados atómicos de archivo). No lo reemplaces por un bind mount al host. El keyring de cifrado también vive en este volumen para que todas las réplicas puedan descifrar.

> **Usuario sin privilegios (desde .NET 10):** la imagen Linux corre como el usuario `app` (UID 1654), no como root. Un volumen nuevo ya tiene ese propietario. Si actualizas desde una versión anterior, el volumen existente es de root y el portal no podrá escribir (`Access to the path ... is denied`); dale el propietario una vez, con los contenedores parados:
>
> ```bash
> docker run --rm -v <volumen>:/data alpine chown -R 1654:1654 /data
> ```

> **Nombre de proyecto `sluice`:** los compose fijan `name: sluice`, así que el volumen es siempre `sluice_pipeline-data`, sea cual sea la carpeta desde la que los ejecutes. Antes, Compose tomaba el nombre del proyecto de la carpeta: si desplegaste desde una carpeta que no se llama `Sluice`, tus datos están en `<proyecto>_pipeline-data`, donde `<proyecto>` es el nombre de esa carpeta en minúsculas (compruébalo con `docker volume ls`), y la actualización arrancaría el stack con un volumen nuevo y vacío. Antes del paso 2, elige una opción:
>
> - **Mantener el nombre anterior:** añade `COMPOSE_PROJECT_NAME=<proyecto>` a `.env` (tiene prioridad sobre `name:`).
> - **Pasar los datos a `sluice_pipeline-data`** (solo con contenedores Linux: los scripts usan un contenedor auxiliar `alpine`; con contenedores Windows, `docker-compose.yml` o `docker-compose.monolith.yml`, usa la primera opción), con el stack anterior parado (`docker compose -p <proyecto> -f <fichero compose> down`), con los [scripts de backup](../BACKUP.es.md):
>
>   ```powershell
>   .\scripts\backup-data-dir.ps1 -VolumeName <proyecto>_pipeline-data -NoEncrypt
>   docker compose -f <fichero compose> create   # crea sluice_pipeline-data sin arrancar nada
>   .\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-<timestamp>.tar.gz -VolumeName sluice_pipeline-data
>   ```
>
>   Después, ejecuta el paso 2 y borra el backup sin cifrar. Conserva el volumen anterior hasta haber comprobado el portal.

## Acceso remoto con TLS

Por defecto los compose publican el portal solo en la propia máquina (`127.0.0.1:58472`): no hace falta certificado, y se entra por `http://localhost:58472` o por un túnel SSH o una VPN. Para abrirlo a otras máquinas pon antes TLS delante. La cookie de sesión es `Secure` fuera de Development, así que por HTTP desde otra máquina el login no funciona.

**Opción A, proxy inverso (recomendada).** Un proxy como Caddy o nginx termina el HTTPS y reenvía a `api:58472` por la red de Docker. Con un nombre DNS público y los puertos 80/443 accesibles, Caddy obtiene y renueva solo un certificado de Let's Encrypt; con un nombre interno, dale el certificado de tu empresa. Ejemplo de `docker-compose.override.yml`:

```yaml
services:
  caddy:
    image: caddy:2
    ports: ["443:443", "80:80"]
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
  api:
    environment:
      KNOWN_NETWORKS: 172.16.0.0/12   # redes de Docker: confiar en el X-Forwarded-For del proxy
volumes:
  caddy-data:
```

con un `Caddyfile` como `sluice.example.com { reverse_proxy api:58472 }`. Pon `KNOWN_PROXIES` o `KNOWN_NETWORKS` con el proxy: sin ellas `X-Forwarded-For` solo se acepta desde loopback, y todos los usuarios comparten la IP del proxy en el límite de intentos de login.

**Opción B, HTTPS en el portal.** Monta un `.pfx` y pon `ASPNETCORE_URLS=https://+:58472;http://localhost:58471`, `ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/sluice.pfx` y `ASPNETCORE_Kestrel__Certificates__Default__Password` en el servicio `api`. El healthcheck del compose sondea HTTP plano, y los workers no arrancan hasta que `api` está sano: sobrescribe el `healthcheck` de `api` con el mismo `test` de tu compose pero con la URL `http://localhost:58471/health` (ese listener HTTP solo responde dentro del contenedor). Después publica en todas las interfaces con `SLUICE_HTTP_BIND=0.0.0.0` en `.env`. La renovación del certificado corre de tu cuenta.

**Arráncalo con los dos ficheros.** Compose solo lee `docker-compose.override.yml` por su cuenta cuando el comando no lleva `-f`, y todos los comandos de esta guía lo llevan. Nombra los dos ficheros, y usa la misma pareja en todos los comandos posteriores (`up`, `down`, `logs` y la actualización del paso 2):

```bash
docker compose -f docker-compose.linux.yml -f docker-compose.override.yml up -d --build
```

En Windows, `-f docker-compose.yml -f docker-compose.override.yml`.

## Primer arranque

En el portal:

1. **Orígenes** → añade tu conexión ADLS y pulsa **Probar**.
2. **Destinos** → añade tu conexión SQL Server y pulsa **Probar**.
3. **Pipelines** → crea un pipeline (origen + destino) y pulsa **Ejecutar** (Incremental).
4. Síguelo en **Panel** / **Jobs**; verifica los conteos en **Conciliación**.

## Escalado

Añade réplicas por etapa cuando una etapa sea el cuello de botella (normalmente SQL o ADLS, no el worker):

```bash
docker compose -f docker-compose.linux.yml up -d --build --scale worker-ingest=3 --scale worker-sql=2
```

## Backups

Todo lo que tiene el portal vive en el volumen `pipeline-data`: las conexiones y sus secretos, el keyring de cifrado, los pipelines, los jobs, la cola y el estado de cada pipeline. Perderlo obliga a configurarlo todo de nuevo, y perder solo el keyring, a volver a escribir el secreto de cada conexión. Haz copias con regularidad, cifradas, y guárdalas fuera del host.

Con contenedores Linux, `scripts/backup-data-dir.ps1` y `scripts/restore-data-dir.ps1` lo hacen con el stack parado. [Backup y recuperación de DATA_DIR](../BACKUP.es.md) tiene los pasos: la clave de cifrado, el backup, el restore y qué comprobar después. Un restore extrae el backup junto a los datos actuales y solo los reemplaza cuando la extracción ha terminado, así que necesita espacio libre para una copia más. Con contenedores Windows los scripts no funcionan; la misma página explica cómo copiar el volumen.

## Ajustes (`/settings`)

Se editan en el portal, se guardan en el volumen y aplican sin reiniciar.

| Ajuste | Controla | Default |
|---|---|---|
| Intervalo de sondeo del scheduler (min) | Cada cuánto el orquestador revisa ADLS y auto-lanza incrementales. | 5 |
| Lease de tareas (s) | Cuánto se retiene una tarea reclamada antes de volver a `pending`. | 120 |
| Umbral de divergencia de reconcile (%) | Por encima, un job se marca Parcial y alerta. | 1.0 |
| Corrección automática | Al terminar una corrida con tablas sobre el umbral (o con diferencias de valores), las re-sincroniza solas, una vez. | Desactivada |
| Retención de histórico (días) | Antigüedad a partir de la cual se purgan jobs y sus artefactos. `0` la desactiva. | 90 |
| Días sin datos para resaltar una tabla | En **Tablas**, *Último dato* se resalta cuando la última carga con filas es más antigua (por ejemplo, una tabla quitada del perfil de Synapse Link). `0` lo desactiva. | 7 |
| Retención de snapshots origen (días) | Antigüedad a partir de la cual se purgan las carpetas CSV ya procesadas del container de Synapse Link. `0` la desactiva. | 30 |
| Simulación de la purga de snapshots | Registra qué se borraría sin borrar nada. | Activada |
| Exigir Parquet antes de purgar | No purga snapshots si falta algún Parquet consolidado de un pipeline que lea esa fuente. | Activada |
| Alertas | Activar y URL del webhook (p. ej. una Azure Logic App); **Enviar prueba** comprueba la URL. | Desactivadas |
| Horas de atraso para alertar | Alerta cuando un snapshot cerrado lleva más de estas horas sin procesarse en un pipeline con scheduler ([detalles](04-reference.es.md#observabilidad-y-alertas)). `0` la desactiva. | 6 |

## Variables de entorno

`UI_API_KEY`, `AUTH_MODE` y las opcionales `ALERT_WEBHOOK_URL`, `DEADMAN_PING_URL`, `CHANGELOG_STALL_HOURS`, `SESSION_EXPIRY_HOURS` y `TRANSLATE_ENUMS` van en `.env`; el resto las fijan los archivos compose.

| Variable | Propósito | Default |
|---|---|---|
| `UI_API_KEY` | Clave compartida para UI + `/api/*` (página `/login` + cookie de sesión). Obligatoria salvo con `AUTH_MODE=none`. | — |
| `AUTH_MODE` | `key` exige `UI_API_KEY`. `none` es acceso abierto a propósito: la landing ofrece **Entrar** sin clave, la API queda abierta, la cabecera muestra **Acceso abierto** y se avisa en el log al arrancar. Úsalo solo en una red interna o detrás de un proxy que ya autentique. Con `none` deja `UI_API_KEY` vacía: las dos juntas no arrancan. | `key` |
| `EXECUTION_MODE` | `local` o `distributed`; otro valor impide arrancar. | lo fija compose |
| `DATA_DIR` | Raíz de persistencia JSON (`/data` Linux, `C:/data` Windows). | lo fija compose |
| `WORKER_STAGE` | Etapa que procesa un worker (`ingest`, `sql` o `reconcile`; otro valor impide arrancar); ausencia = orquestador. Un worker solo responde `/health`: no sirve la UI ni `/api/*`, así que no necesita `UI_API_KEY`. | lo fija compose |
| `KNOWN_PROXIES` / `KNOWN_NETWORKS` | IPs y redes CIDR (separadas por comas) del proxy inverso de confianza para `X-Forwarded-For`/`Proto`. Sin ellas solo se acepta desde loopback; detrás de un proxy en otro contenedor configúralas, o todos los usuarios comparten la IP del proxy en el límite de intentos de login (se avisa al arrancar). Un valor inválido impide arrancar. Ver *Acceso remoto con TLS*. | vacío |
| `HEARTBEAT_MAX_FAILURES` | Fallos de heartbeat antes de liberar una tarea. | 3 |
| `CHANGELOG_STALL_HOURS` | Horas sin actualizar el changelog antes de alertar. | 16 |
| `ALERT_WEBHOOK_URL` | Webhook de alertas que se usa cuando el de **Ajustes** está apagado o vacío, y cuando `settings.json` no se puede leer. El api y los workers envían alertas, así que los compose se la pasan a todos. | vacío |
| `DEADMAN_PING_URL` | URL a la que llama el scheduler al final de cada ciclo sano, para un dead-man switch externo ([detalle](04-reference.es.md#observabilidad-y-alertas)). | vacío |
| `SESSION_EXPIRY_HOURS` | Horas que dura una sesión iniciada. | 12 |
| `TRANSLATE_ENUMS` | Añade una columna `[<col>_$label]` con la etiqueta de cada valor de enum (idioma 1033), leída del `GlobalOptionsetMetadata.csv` del snapshot. Solo la usa la etapa SQL. | `true` en `docker-compose.yml` y `docker-compose.linux.yml`, `false` en `docker-compose.monolith.yml` |
| `LOCAL_SOURCE_ROOT` | Solo para la demo y las pruebas: habilita el tipo de origen *Local (UAT)*, que lee carpetas locales en vez de ADLS. **Nunca se usa en producción**; solo lo define `docker-compose.demo.yml`. | — |
| `DEMO_MODE` | Modo demo (`docker-compose.demo.yml`): genera datos sintéticos de F&O en `LOCAL_SOURCE_ROOT` y crea la base, las conexiones y el pipeline de la demo. Necesita `LOCAL_SOURCE_ROOT` y `DEMO_SQL_PASSWORD`. **Nunca en producción.** | `false` |
| `DEMO_SQL_PASSWORD` | Contraseña del usuario `sa` del SQL Server de la demo. | — |
| `DEMO_SNAPSHOT_MINUTES` | Minutos entre snapshots sintéticos en el modo demo. | 5 |

Las alertas se configuran en **Ajustes** (`/settings`): activa las alertas y pega la URL del disparador HTTP de tu Azure Logic App. Si aún no tienes la Logic App, **Ajustes → Alertas por correo (Logic App) → Descargar paquete** trae una lista para desplegar que envía cada alerta por correo de Office 365 (sigue su `README.md`).

## Conexiones

Se crean en `/sources` y `/destinations`. Rellena los campos del `Config`; los secretos se cifran at-rest y se muestran como `***`. Si cambias a dónde apunta una conexión (el `host` de SQL, o la `account`, el `tenant_id` o el `client_id` de ADLS), vuelve a escribir su secreto: el guardado no se envía a otro destino. Mientras un pipeline que usa la conexión tenga un job en curso, Sluice no deja cambiar a dónde apunta (tampoco su `database`, `container`, `parquet_container` ni dónde guarda el Parquet). Si cambias el tipo de una conexión existente, no se arrastra su configuración anterior: solo se guardan los campos del tipo nuevo.

- **Origen ADLS:** `account`, `container`, `parquet_container`, `tenant_id`, `client_id`, `client_secret`. La cuenta y los containers deben ser nombres válidos de Azure Storage (minúsculas y dígitos; los containers admiten también guiones).
- **Dónde guardar el Parquet** (por origen): en un **contenedor de ADLS** (`parquet_container`, por defecto) o en el **disco del portal** (`DATA_DIR/parquet`, el volumen que ya comparten el portal y los workers). En el disco del portal no hace falta contenedor de Parquet ni permiso de escritura en ADLS, pero ahí se guarda el Parquet consolidado de todas las tablas: dimensiona el volumen de `DATA_DIR` para ello (más sus versiones anteriores, ver *Versiones del Parquet consolidado* en la referencia) e inclúyelo en las copias de seguridad. Al cambiarlo, Sluice copia el Parquet consolidado al sitio nuevo antes de usarlo y conserva la copia anterior; si la copia falla, el origen queda como estaba.
- **Destino SQL:** `host`, `database`, `user`, `password`, **Confiar en el certificado del servidor** (`trust_server_certificate`, desmarcada por defecto: se valida el certificado del servidor; márcala solo para un SQL Server con certificado autofirmado que no puedas sustituir. Los destinos creados antes de esta opción siguen confiando en él) y `sql_version` (versión de SQL Server, default 2022). La versión no hace falta elegirla: **Probar conexión** en el formulario la detecta del servidor y la ajusta (sin guardar), y **Probar** desde la fila corrige la guardada si no coincide. Marcar **Confiar en el certificado del servidor** en un destino guardado pide de nuevo su contraseña: la guardada no se envía a un servidor cuyo certificado ya no se comprueba. Ya no se admite un `connection_string` completo. En el primer arranque tras actualizar, un destino que lo tuviera recibe de él sus campos (host, base de datos, usuario, contraseña y la opción del certificado). Si usaba otras opciones (por ejemplo `Integrated Security` o `Authentication`), la cadena se quita, el registro de actividad dice qué opciones lo impidieron y sus pipelines no arrancan hasta que escribas sus campos en **Destinos**.

Usa **Probar conexión** en el formulario (origen o destino) para validar contra el ADLS/SQL real antes de guardar, o **Probar** desde la fila para una conexión ya guardada, antes de ejecutar un pipeline. En un origen, la prueba comprueba la cuenta, el contenedor y el changelog de Synapse Link. Las sentencias SQL de un pipeline no tienen límite de tiempo.

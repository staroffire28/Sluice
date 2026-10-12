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

- El **disco** lo domina el volumen `pipeline-data` (jobs, reportes, staging de parquet). Está acotado por la retención (90 días por defecto). Usa SSD para la cola.
- La **RAM** tiene su pico en la consolidación de parquet (un snapshot a la vez). 16 GB es cómodo para varios pipelines en paralelo.
- SQL Server y ADLS corren en otra parte y no se cuentan aquí.

## Prerrequisitos

- **Docker Engine 24+** con Docker Compose v2.
- **Host Linux**: cualquier distro con Docker (Linux containers, el modo por defecto del daemon).
- **Host Windows**: Docker Desktop en modo **Windows containers** (Windows Server 2022 o Windows 11 + Hyper-V).
- Un valor para `UI_API_KEY` en `.env` (o `AUTH_MODE=none` si quieres el acceso abierto, sin clave; ver Variables de entorno).

Las credenciales de Azure/SQL **no** se ponen aquí: se configuran en el portal después de arrancar.

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

## Ajustes (`/settings`)

Se editan en el portal, se guardan en el volumen y aplican sin reiniciar.

| Ajuste | Controla | Default |
|---|---|---|
| Intervalo de sondeo del scheduler (min) | Cada cuánto el orquestador revisa ADLS y auto-lanza incrementales. | 5 |
| Lease de tareas (s) | Cuánto se retiene una tarea reclamada antes de volver a `pending`. | 120 |
| Umbral de divergencia de reconcile (%) | Por encima, un job se marca Parcial y alerta. | 1.0 |
| Corrección automática | Al terminar una corrida con tablas sobre el umbral (o con diferencias de valores), las re-sincroniza solas, una vez. | Desactivada |
| Retención de histórico (días) | Antigüedad a partir de la cual se purgan jobs y sus artefactos. `0` la desactiva. | 90 |
| Retención de snapshots origen (días) | Antigüedad a partir de la cual se purgan las carpetas CSV ya procesadas del container de Synapse Link. `0` la desactiva. | 30 |
| Simulación de la purga de snapshots | Registra qué se borraría sin borrar nada. | Activada |
| Exigir Parquet antes de purgar | No purga snapshots si falta algún Parquet consolidado del pipeline. | Activada |
| Alertas | Activar y URL del webhook (p. ej. una Azure Logic App); **Enviar prueba** comprueba la URL. | Desactivadas |

## Variables de entorno

`UI_API_KEY` y `AUTH_MODE` van en `.env`; el resto las fijan los archivos compose.

| Variable | Propósito | Default |
|---|---|---|
| `UI_API_KEY` | Clave compartida para UI + `/api/*` (página `/login` + cookie de sesión). Obligatoria salvo con `AUTH_MODE=none`. | — |
| `AUTH_MODE` | `key` exige `UI_API_KEY`. `none` es acceso abierto a propósito: la landing ofrece **Entrar** sin clave, la API queda abierta, la cabecera muestra **Acceso abierto** y se avisa en el log al arrancar. Úsalo solo en una red interna o detrás de un proxy que ya autentique. Con `none` deja `UI_API_KEY` vacía: las dos juntas no arrancan. | `key` |
| `EXECUTION_MODE` | `local` o `distributed`. | lo fija compose |
| `DATA_DIR` | Raíz de persistencia JSON (`/data` Linux, `C:/data` Windows). | lo fija compose |
| `WORKER_STAGE` | Etapa que procesa un worker (`ingest`, `sql` o `reconcile`; otro valor impide arrancar); ausencia = orquestador. | lo fija compose |
| `KNOWN_PROXIES` / `KNOWN_NETWORKS` | IPs y redes CIDR (separadas por comas) del proxy inverso de confianza para `X-Forwarded-For`/`Proto`. Sin ellas se acepta de cualquier origen y la IP del límite de intentos de login se puede falsificar (se avisa al arrancar); un valor inválido impide arrancar. | vacío |
| `HEARTBEAT_MAX_FAILURES` | Fallos de heartbeat antes de liberar una tarea. | 3 |
| `CHANGELOG_STALL_HOURS` | Horas sin actualizar el changelog antes de alertar. | 16 |
| `LOCAL_SOURCE_ROOT` | Solo para pruebas UAT: habilita el tipo de origen *Local (UAT)*, que lee carpetas locales. **Nunca se usa en producción**; no está en los archivos compose ni en `.env.example`. | — |

Las alertas se configuran en **Ajustes** (`/settings`): activa las alertas y pega la URL del disparador HTTP de tu Azure Logic App. Si aún no tienes la Logic App, **Ajustes → Alertas por correo (Logic App) → Descargar paquete** trae una lista para desplegar que envía cada alerta por correo de Office 365 (sigue su `README.md`).

## Conexiones

Se crean en `/sources` y `/destinations`. Rellena los campos del `Config`; los secretos se cifran at-rest y se muestran como `***`. Si cambias el tipo de una conexión existente, no se arrastra su configuración anterior: solo se guardan los campos del tipo nuevo.

- **Origen ADLS:** `account`, `container`, `parquet_container`, `tenant_id`, `client_id`, `client_secret`.
- **Destino SQL:** `host`, `database`, `user`, `password` y `sql_version` (versión de SQL Server, default 2022). La versión no hace falta elegirla: **Probar conexión** en el formulario la detecta del servidor y la ajusta (sin guardar), y **Probar** desde la fila corrige la guardada si no coincide. Un `connection_string` completo se usa solo si la conexión ya lo trae; el formulario no tiene campo para él.

Usa **Probar** para validar contra el ADLS/SQL real antes de ejecutar un pipeline. Las sentencias SQL de un pipeline no tienen límite de tiempo por defecto; un `connection_string` con su propio `Command Timeout` se respeta.

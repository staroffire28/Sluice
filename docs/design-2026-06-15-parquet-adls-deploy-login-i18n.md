# Design Doc — Parquet a ADLS, deploy distribuido, quitar login, i18n ES/EN

- **Fecha:** 2026-06-15
- **Proyecto:** Sluice (.NET 8 / Blazor Server) — pipeline Synapse Link → ADLS Gen2 → Parquet → SQL Server
- **Modo:** office-hours (intrapreneurship / herramienta interna)
- **Estado:** W1 (parquet→ADLS, Approach B), W2 (deploy distribuido por defecto) y
  W3 (quitar login) **implementados y verificados** (build + 72 tests, contenedor en vivo).
  W4 (i18n ES/EN): **completo y verificado en vivo** — infraestructura + cromo + encabezados
  + texto de cuerpo (labels, columnas, botones, vacíos, diálogos). 186 claves localizadas,
  ES/EN balanceados, toggle ES↔EN funciona en toda la app. **Los 4 workstreams están hechos.**

---

## Contexto

El portal orquesta un pipeline de datos en contenedores Windows. El usuario pide cuatro
cambios independientes. Durante la exploración apareció una **brecha clave**: el parquet
hoy NO vive en ADLS, sino en el disco local del contenedor (volumen `pipeline-data`),
en `{DataDir}/{pipelineId}/runs/{jobId}/parquet/{entity}.parquet`. ADLS hoy es solo de
**lectura** (los CSV de Synapse Link). El cliente `IAdlsClient` ni siquiera tiene
operaciones de escritura o borrado.

## Decisiones (confirmadas)

| # | Decisión | Elección |
|---|----------|----------|
| D1 | Destino del parquet | Container ADLS dedicado + **gestión en el portal** (listar/descargar/borrar) |
| D2 | Modo de deploy por defecto | **Distribuido** (api + workers separados); monolito como opción de smoke test |
| D3 | Login de UI | **Quitar** la auth de usuario; UI abierta en red interna confiable |
| D4 | Idioma | **i18n real ES/EN** (localización completa + selector en el header) |
| — | Enfoque parquet→ADLS | **Approach B: ADLS como store canónico** (ingest escribe a ADLS, SQL lee de ADLS) |

## Premisas (aceptadas)

1. El container de destino del parquet es **dedicado** (distinto del que gestiona la
   integración de Synapse Link) y vive en la **misma** storage account ya configurada
   (reusa credenciales). Sobrevive aunque se borre la integración.
2. No hay copia local **persistente** del parquet. (Nota técnica abajo: Parquet.Net
   necesita un stream seekable para escribir, así que habrá un archivo temporal efímero
   que se sube a ADLS y se borra; lo canónico es ADLS y SQL lee de ahí.)
3. Quitar el login de UI NO debilita la auth entre servicios: `/internal/*` sigue
   exigiendo `WORKER_API_KEY`. El portal queda accesible a cualquiera con acceso de red.
4. i18n ES/EN es deuda de mantenimiento permanente: todo texto nuevo deberá ir a recursos.
5. El borrado de parquet desde el portal es **destructivo e irreversible**; llevará
   confirmación explícita.

---

## W1 — Parquet canónico en ADLS (Approach B) + gestión en el portal

### Estado actual
- Escritura: `Services/Pipeline/ParquetMaterializer.cs:15` (a disco local), invocado en
  `Workers/IngestExecutor.cs:160`.
- Ruta: `Services/Pipeline/IngestManifestStore.cs:18` →
  `{DataDir}/{pipelineId}/runs/{jobId}/parquet/{entity}.parquet`.
- Lectura para SQL: `Workers/SqlExecutor.cs:84` (`ParquetMaterializer.ReadAsync(entry.ParquetPath, ...)`).
- Conexión ADLS: `record AdlsConnectionSettings(Account, Container, TenantId, ClientId, ClientSecret)`
  en `Services/Connections/ConnectionResolver.cs:44`; `ResolveAdls` lee la key `"container"`.
- `IAdlsClient` (`Services/Adls/IAdlsClient.cs`): **solo lectura**.

### Estado objetivo
- El ingest materializa el parquet **directo al container ADLS de destino**.
- El stage SQL lee el parquet **desde ADLS**.
- El manifiesto guarda la ubicación ADLS (container + ruta de blob) en vez de ruta local.
- El portal expone listar / descargar / borrar (con confirmación) sobre ese container.

### Cambios de diseño
1. **Modelo de conexión** (`ConnectionResolver.cs`, `Models/Connection.cs`): añadir key
   `parquet_container` (container de destino). Extender `AdlsConnectionSettings` con
   `DestinationContainer`. Fallback a env `AZURE_PARQUET_CONTAINER` / `AppOptions`.
2. **`IAdlsClient` + `AdlsClient`**: añadir operaciones de escritura/gestión sobre el
   container de destino:
   - `UploadAsync(container, blobPath, Stream)` (subida del parquet)
   - `OpenReadAsync(container, blobPath) : Stream` (lectura para SQL)
   - `DeletePathAsync(container, blobPath)`
   - `ListAsync(container, prefix) : IReadOnlyList<ParquetArtifact>` (ruta, tamaño, lastModified)
   - Requiere permiso **Storage Blob Data Contributor** sobre el container de destino
     (hoy solo se usa lectura → ver Assignment).
3. **`ParquetMaterializer`**: overloads que escriban a/lean desde `Stream`
   (`WriteToStreamAsync` / `ReadFromStreamAsync`) para no depender de rutas locales.
   Nota: Parquet.Net necesita stream seekable al escribir → materializar a temp local
   o `MemoryStream`, subir a ADLS, borrar el temp.
4. **`IngestExecutor.cs:160`**: en vez de escribir a ruta local, materializar a temp y
   subir a `{parquet_container}/{pipelineId}/{jobId}/{entity}.parquet`; registrar esa
   ubicación ADLS en el manifiesto.
5. **`IngestManifestStore` / entrada de manifiesto**: cambiar la semántica de
   `ParquetPath` a ubicación ADLS (container + blob). El manifiesto JSON sigue siendo
   estado local (no se mueve).
6. **`SqlExecutor.cs:84`**: leer el parquet vía `AdlsClient.OpenReadAsync(...)` →
   `ParquetMaterializer.ReadFromStreamAsync(...)`. **Este es el camino crítico de carga**;
   cubrir con tests.
7. **UI de gestión**: nueva sección/página que liste artefactos parquet por
   pipeline/job con descargar y **borrar** (confirmación). Endpoints en
   `Endpoints/ManagementEndpoints.cs` (GET listar, GET descargar, DELETE borrar).

### Riesgos
- **Toca el camino crítico de carga a SQL** (lectura desde ADLS): es el mayor riesgo de B.
  Mitigar con tests de integración del stage SQL leyendo de ADLS (o un fake de `IAdlsClient`).
- Latencia de red al leer parquet grande en cada carga; considerar streaming.
- Permisos de escritura/borrado en ADLS que hoy no existen (ver Assignment).
- Reruns sobreescriben el mismo blob → definir si se versiona o se sobreescribe.
- Interacción con D3: los endpoints de gestión de parquet quedan **sin auth** (red interna).

---

## W2 — Deploy distribuido por defecto

### Estado actual
- `docker-compose.yml` ya despliega `api` + `worker-ingest` + `worker-sql` +
  `worker-reconcile` como contenedores separados (cola atómica y réplicas resueltas en T5/T6).
- `docker-compose.monolith.yml` = un solo contenedor (`EXECUTION_MODE=local`).

### Estado objetivo
- El distribuido es el camino principal documentado; el monolito queda como atajo para
  smoke test local.

### Cambios de diseño
- Documentar el distribuido como default (README / instrucciones de arranque).
- Revisar `scripts/redeploy.ps1` (hoy reconstruye el monolito) → variante distribuida o
  aclarar para qué sirve cada uno.
- Asegurar que `.env.example` cubre el flujo distribuido (incluye `parquet_container`/`AZURE_PARQUET_CONTAINER` de W1).
- **Sin trabajo de infra nuevo**; es defaults + docs.

### Riesgos
- Bajo. Confirmar que el healthcheck/`depends_on` siguen correctos al volverlo el default.

---

## W3 — Quitar el login de UI (mantener API key entre workers)

### Estado actual — auth dual
- Cookie de sesión UI (`UI_API_KEY` + página `/login`) y API key entre servicios
  (`WORKER_API_KEY` para `/internal/*`).
- Piezas: `Middleware/DualAuthMiddleware.cs`, `Services/Auth/AuthService.cs`,
  `Services/Auth/SessionTokenService.cs`, `Endpoints/AuthEndpoints.cs`,
  `Components/Pages/Login.razor`, `Components/Layout/LoginLayout.razor`,
  `Components/Layout/MainLayout.razor` (botón logout), `Program.cs:70-95`.

### Estado objetivo (D3 = UI abierta)
- Se elimina toda la auth de **usuario** de la UI; cualquiera con red al contenedor entra.
- Se **mantiene** la protección de `/internal/*` por `WORKER_API_KEY`.

### Cambios de diseño
- `DualAuthMiddleware.cs`: eliminar la rama de sesión UI (redirección a `/login` para
  páginas y `/api/*`); **conservar** la rama que valida `WORKER_API_KEY` en `/internal/*`.
- Eliminar: `Login.razor`, `LoginLayout.razor`, botón logout en `MainLayout.razor`,
  endpoints `/auth/login` y `/auth/logout` (`AuthEndpoints.cs`), y el `SessionTokenService`
  / `AuthService` si dejan de usarse para UI.
- `Program.cs`: quitar `MapAuthEndpoints` y el warning de "auth disabled".

### Riesgos (importante)
- `/api/*` (`ManagementEndpoints`, incluye gestión de conexiones con secretos y disparo de
  pipelines) queda **abierto en la red**. Los secretos siguen cifrados at-rest, pero
  cualquiera en la red puede leer/editar conexiones y lanzar runs.
- **Recomendación operativa:** NO publicar el puerto del portal fuera de la red interna;
  bindear a interfaz interna / detrás de VPN o reverse proxy. Documentarlo.

---

## W4 — i18n ES/EN

### Estado actual
- No hay localización: ni `AddLocalization`, ni `RequestLocalization`, ni `IStringLocalizer`,
  ni `.resx`. La app está en español hardcodeado. El header tiene HealthPill + ThemeToggle
  + logout; no hay selector de idioma.

### Estado objetivo
- Selector ES/EN en el header (junto al ThemeToggle), persistido por cookie de cultura;
  todos los textos visibles localizados.

### Cambios de diseño
1. `Program.cs`: `AddLocalization(o => o.ResourcesPath = "Resources")` +
   `RequestLocalizationOptions` con culturas `es` (default) y `en`, vía
   `CookieRequestCultureProvider`; `app.UseRequestLocalization(...)`.
2. `Resources/`: crear `.resx` (un `SharedResource` o por componente). Extraer **todos**
   los textos hardcodeados a recursos e inyectar `IStringLocalizer`.
3. Header: componente `LanguageToggle.razor` (espejo de `ThemeToggle.razor`,
   `@rendermode InteractiveServer`) que fija la cookie de cultura y recarga.
4. Inventario de superficies con texto a traducir: `MainLayout`, todas las páginas de
   `Components/Pages/`, componentes de `Components/Shared/`, labels de `ThemeToggle`,
   mensajes de estado del pipeline.

### Riesgos
- Deuda de mantenimiento permanente (todo texto nuevo va a recursos).
- Localización en Blazor Server con render `InteractiveServer`: el cambio de cultura por
  cookie suele requerir recarga del circuito; validar el flujo del selector.
- Trabajo mecánico amplio (extracción de strings).

---

## Secuencia recomendada

Workstreams independientes. Orden sugerido por riesgo/dependencia:

1. **W3 (quitar login)** y **W2 (deploy default)** primero — baratos y rápidos. Hacer W3
   *antes* de construir la UI/endpoints de gestión de parquet (W1) para no meterles auth
   que luego quitamos.
2. **W1 (parquet → ADLS, Approach B)** — el núcleo; toca el camino crítico de carga.
   Implementar con tests del stage SQL leyendo de ADLS.
3. **W4 (i18n)** — mecánico y amplio; al final para no reescribir strings que cambien en W1–W3.

## Preguntas abiertas (resolver al implementar)

- W1: ¿los reruns sobreescriben el blob del parquet o se versiona por timestamp?
- W1: ¿retención/limpieza automática del container de destino, o solo borrado manual desde el portal?
- W4: ¿confirmas solo ES/EN, o habrá más idiomas? (afecta el diseño de recursos)

---

## The Assignment (acción concreta antes de implementar W1)

**Crea manualmente el container ADLS de destino y verifica permisos de escritura/borrado
ANTES de escribir código de W1.** Hoy la identidad (`client_id`) solo necesita lectura;
Approach B requiere **escribir y borrar** en el container de destino. En tu storage account:

1. Crea el container dedicado (p. ej. `synapse-parquet`).
2. Asigna a la app registration (`client_id`) el rol **Storage Blob Data Contributor**
   sobre ese container (no solo lectura).
3. Verifícalo con Azure Storage Explorer o `az storage blob upload`/`delete` usando esas
   credenciales.

Si ese permiso no está, W1-B falla en runtime. Confírmame el **nombre del container** y que
el **rol de escritura/borrado** está asignado, y arrancamos por W3+W2.

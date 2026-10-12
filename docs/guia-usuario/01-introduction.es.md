# Introducción

Sluice Portal opera y observa un pipeline de datos que copia datos desde **Azure Synapse Link (ADLS Gen2)** hacia **SQL Server**, manteniendo una copia **Parquet** duradera en el medio:

```
ADLS Gen2 (Synapse Link)
↓
Parquet consolidado
↓
SQL Server (MERGE)
↓
Conciliación
```

Desde el portal creas conexiones, defines pipelines, lanzas corridas (manuales o programadas), sigues su progreso en vivo y verificas que SQL haya quedado igual que el origen. El portal en sí no tiene base de datos: todo su estado vive como archivos JSON en un volumen (`DATA_DIR`).

## Para quién es

- **Operadores** que usan el portal en el día a día: ver [Funcionalidad](02-functionality.md).
- **Quien instala y ejecuta la infraestructura**: ver [Instalación](03-installation.md).
- **Cualquiera que necesite saber qué significa un término**: ver el glosario en [Referencia](04-reference.md).

## Conceptos clave de un vistazo

- **Pipeline** — la definición "de este origen ADLS a este destino SQL".
- **Job (corrida)** — una ejecución de un pipeline.
- **Snapshot** — una carpeta de exportación de Synapse Link en ADLS (una "foto" de los datos).
- **Conciliación (Reconcile)** — la verificación de que SQL tiene las mismas filas que el Parquet.
- **Divergencia** — el porcentaje en que SQL y el origen no coinciden.

Las definiciones completas están en [Referencia · Glosario](04-reference.md#glosario).

## Acceso e idioma

Por defecto el portal usa una **clave de acceso compartida** (`UI_API_KEY`): la página de inicio pide la clave y deja una cookie de sesión para la UI y `/api/*`. También puede desplegarse en **acceso abierto** (`AUTH_MODE=none`): la página de inicio muestra **Entrar** sin clave y la cabecera indica **Acceso abierto**; úsalo solo en una red interna. En ningún caso expongas su puerto a internet. Los workers no necesitan clave: comparten la cola de tareas en el volumen de `DATA_DIR`.

El portal arranca en **inglés** y puede cambiar a **español** con el botón de idioma de la cabecera (esquina superior derecha), junto al de tema claro/oscuro.

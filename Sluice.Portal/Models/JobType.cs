namespace Sluice.Portal.Models;

public enum JobType
{
    Incremental,
    // Recarga SQL desde parquet consolidado (salta ingest). Ya no re-lee CSV histórico en ADLS.
    Force,
    Reconcile,
    Diff,
    ResyncTable,
    ReloadFromParquet,

    // Comparación de valores bajo demanda (hash por fila parquet vs SQL); solo etapa de conciliación.
    CompareValues,
}

// Modo de remediación de una tabla divergente:
//  - Reload: upsert del parquet consolidado + borrado de filas huérfanas (Id que ya no
//    está en el parquet). Corrige divergencia en ambas direcciones sin vaciar la tabla
//    (la tabla nunca queda vacía, así que no hay ventana de crash).
//  - Remerge: solo upsert del parquet consolidado. Más ligero; solo corrige cuando a SQL
//    le faltan filas (source > dest), no las huérfanas.
public enum ResyncMode
{
    Reload,
    Remerge,
}
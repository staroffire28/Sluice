namespace Sluice.Portal.Models;

public sealed record StageQueueCounts(int Pending, int Claimed, int Done, int Error);

public sealed record QueueSnapshot(StageQueueCounts Ingest, StageQueueCounts Sql, StageQueueCounts Reconcile);
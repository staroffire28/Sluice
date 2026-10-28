# Back up and restore DATA_DIR

[Español](BACKUP.es.md)

## Why

Sluice Portal has no database: all its operational state (connections with their secrets
encrypted at rest, pipelines, jobs, the task queue, reconcile/diff reports, and the ASP.NET
Data Protection keyring that decrypts the connection secrets) lives in the Docker volume
`pipeline-data` (`DATA_DIR`). It is a single volume with no replica: losing or corrupting it
loses all the portal's state, and losing the keyring in particular makes the saved ADLS/SQL
credentials **unrecoverable**, even if the rest of the volume survives intact.

## What the backup includes

All of `DATA_DIR`: `connections/`, `pipelines/`, `jobs/`, `tasks/`, `reports/`, `keys/`
(the Data Protection keyring), `workers/`, each pipeline's state (`{pipelineId}/state/`) and
`parquet/` (the consolidated Parquet of the sources that keep it on the portal disk, with
its earlier versions in `parquet/_versions/`).
It does not include the consolidated Parquet kept in ADLS or the ADLS snapshots: those live
in Azure, outside the local volume, with their own durability and replication managed by
Azure Storage.

## The encryption key

The backup carries the connection secrets **and** the keyring that decrypts them, so it is
stored encrypted with [age](https://age-encryption.org) (`winget install FiloSottile.age`,
`brew install age` or your distribution's package). Generate the key pair once, in your user
folder and not in the repo's, so it never ends up in a commit or an image:

```powershell
age-keygen -o "$HOME/sluice-backup.key"
# Public key: age1...   <- the public key, for -Recipient
```

- The **public key** (`age1...`) is not secret: it can stay on the host and in the scheduled
  task that makes the backups.
- The **private key** (`sluice-backup.key`) is the only way to restore. Keep it **off** the
  host and away from the backups: in a password manager or a secrets vault, with a second
  copy somewhere else. Whoever has a backup and the private key has the ADLS/SQL
  credentials; whoever loses the private key loses every backup.
  Delete `$HOME/sluice-backup.key` from the host as soon as you have stored it.

## How to back up

Stop the stack before the backup: with the containers writing (task queue, jobs, state), the
tar can end up half written and inconsistent.

```powershell
# 1. Stop the portal and the workers (the containers are kept):
docker compose -f docker-compose.linux.yml stop

# 2. Encrypted backup for your public key:
.\scripts\backup-data-dir.ps1 -Recipient age1...
# or with explicit parameters:
.\scripts\backup-data-dir.ps1 -Recipient age1... -VolumeName sluice_pipeline-data -OutputDir .\backups

# 3. Start them again:
docker compose -f docker-compose.linux.yml start
```

Without `-Recipient` the script fails. For an unencrypted backup (for example, to move the
data to another volume on the same host), pass `-NoEncrypt` and delete the tar as soon as
you are done.

Without `-VolumeName`, the backup and restore scripts find the volume by the label Compose
gives it (`com.docker.compose.volume=pipeline-data`), whatever the project name. If they find
none, or several (for example, one from an earlier deployment in another folder), they fail
without touching anything and ask you to name it with `-VolumeName`.

It writes `backups\pipeline-data-<timestamp>.tar.gz.age` (the unencrypted tar is written to a
temporary folder and deleted as soon as age finishes or fails: it never reaches
`-OutputDir`). Keep this copy **off** the host (another disk, blob storage, etc.): a backup on
the same disk as the original volume does not protect against a disk failure.

Recommended: automate the three steps (stop, back up, start) with Windows Task Scheduler or
an equivalent cron job (for example daily, at an hour with no scheduled loads), rotating and
purging old backups according to your retention policy.

The scripts need Linux containers: they run a small `alpine` helper container. With Windows
containers, stop the stack, find the volume's folder on the host with
`docker volume inspect sluice_pipeline-data --format "{{ .Mountpoint }}"`, and copy and
encrypt that folder with your usual backup tool.

## How to restore

```powershell
# 1. Stop the portal and the workers (nothing writes while it restores):
docker compose -f docker-compose.linux.yml down

# 2. Restore with the private key (replaces ALL the current content of the volume, asks for confirmation):
.\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-20260701120000.tar.gz.age -Identity $HOME\sluice-backup.key

# 3. Start it again and check:
docker compose -f docker-compose.linux.yml up -d
curl http://localhost:58472/health
curl http://localhost:58472/health/ready   # also checks real ADLS/SQL connectivity
```

Before touching the volume, the script decrypts the backup and lists the whole tar: if the
private key is wrong or the backup is damaged, it fails without deleting anything. Then it
extracts it into `/data/.restore-new` and replaces the current content only when the
extraction has finished: if the disk fills up halfway, the volume stays as it was. That is
why it needs free space for one more copy of the data. The decrypted tar is deleted at the
end.

After restoring, check in the portal (Sources/Destinations → **Test**) that the existing
connections decrypt correctly. The Data Protection keyring travels with the secrets in the
same backup, so a consistent restore (the same backup for both) should always decrypt.

## RTO/RPO (indicative, adjust to your real operation)

- **RPO** (how much you can lose): the backup frequency. With a daily backup, up to 24 hours
  of jobs and configuration changes in the worst case.
- **RTO** (how long recovery takes): minutes. The script only packs and unpacks the volume;
  most of the time goes into starting the containers again.

## Important

**Test the restore regularly on a test volume.** A backup that was never restored is not a
verified backup: it may be corrupt or incomplete, or the script may have stopped working
after a change in the environment without anyone noticing until it is really needed.

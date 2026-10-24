# 12. Backup and Recovery

## 12.1 Weighbridge PC

**What is backed up**: the complete SQLite database (online backup, consistent while the application runs) and,
if *include evidence* is on (default), the camera images. The result is one `.awmsbak` file (ZIP with a manifest:
application version, schema version, counts, database SHA-256) plus a `.sha256` file for the archive.

**Schedule** (Administration → Backup, or Settings → Backup):

| Setting | Default |
|---|---|
| Daily backup | on, at 23:30; if the PC was off at that time, the backup is made as soon as the application runs again (one catch-up backup, not one per missed day) |
| Folder | `C:\ProgramData\AdvancedWeighbridge\backups` |
| Second copy | none — **set a USB drive or network share**; a copy on the same disk does not protect against disk failure or theft |
| Keep | 30 backups per folder (older backup files are deleted; nothing else is) |
| Include evidence images | on |

*Backup now* makes an immediate backup (needs the *manage backups* permission). Every backup, its size, SHA-256
and result are listed in the history and written to the audit trail; a failed backup or a failed second copy
raises an alert (also shown in the cloud and the app). The cloud dashboard shows each site's last backup time.

**Recommended practice**: second copy on a USB drive that is swapped weekly and stored off site, or on a network
share that is itself backed up; test a restore every three months on a spare PC (12.3).

## 12.2 Restore on the same PC

1. Administration → Backup → *Restore…*, choose the `.awmsbak` file (administrator; confirmation required).
2. The archive is verified first (SHA-256 of the archive, manifest, database integrity check). A damaged or
   modified backup is refused.
3. The current database is moved to a `pre-restore-<time>` folder (nothing is deleted), the backup is restored,
   migrated to the current schema if it is older, and the restore is recorded in the audit trail.
4. The application closes; start it again.

Transactions recorded after the backup are not in the restored database; they remain in the pre-restore copy. If
the site uses the cloud, those transactions are also in the cloud. Re-enter missing transactions only as new
tickets with a remark referencing the original ticket numbers.

## 12.3 Move to a new PC (or recover after a disk failure)

1. Install the same or a newer AWMS version on the new PC (`10-installation-guide.md`).
2. Start it once and complete the first-run window with temporary values (they are replaced by the restore).
3. Sign in with the temporary administrator, Administration → Backup → *Restore…* with the latest backup; the
   application closes; start it again and sign in with the accounts from the backup.
4. Secrets protected with Windows DPAPI (cloud site key, camera passwords, authenticator secrets of desktop users)
   are bound to the old PC and cannot be decrypted on a new one. Enter the cloud site key again (or rotate it in
   the cloud), re-enter camera passwords, and reset two-factor sign-in for users who had it.
5. Check the indicator profile and run tests A4–A7 of the acceptance procedure.

Alternative without the application: the `.awmsbak` file is a ZIP; `weighbridge.db` inside it is a standard SQLite
database that can be opened read-only with any SQLite tool for inspection.

## 12.4 Cloud server

Back up the PostgreSQL database and the `clouddata` volume (signing and encryption keys):

```sh
docker compose exec -T db pg_dump -U awms -Fc awms > awms-cloud-$(date +%F).dump
docker run --rm -v cloud_clouddata:/data -v "$PWD":/backup alpine tar czf /backup/awms-clouddata-$(date +%F).tgz -C /data .
```

(The volume name is prefixed with the compose project name, `cloud_` when started from the `cloud` folder; see
`docker volume ls`.) Restore: create the volumes, `pg_restore -U awms -d awms --clean` the dump into the `db`
container, unpack the `clouddata` archive into its volume, start the stack. Keep the backups encrypted and off
the server.

If the cloud data is lost entirely, the site PCs still hold the complete records. After a new cloud is set up with
the same site codes and new site keys, each site uploads changes made from then on; history that had already been
uploaded is **not re-sent automatically in version 1.0** (an outstanding item). It remains complete on the site
PCs, in their reports and in their backups.

## 12.5 Android app

The app stores no business data: everything comes from the cloud. The encrypted sign-in token is excluded from
Android backups and device transfers; after a phone change the user simply signs in again.

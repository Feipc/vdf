# Database Safety and Backups Design

## Problem

The Web application restores `WebResults.json` at startup without first loading
`ScannedFiles.db`. The restored result list makes delete, move, and link actions
available while the process-wide in-memory scan database is still empty.

Those actions currently mutate the empty in-memory database and then call
`SaveDatabase()`. This replaces the complete on-disk database with a small,
mostly empty database. The next scan therefore has no cached frame hashes and
must run FFmpeg sampling again for the entire media collection.

## Scope

This change protects the scan database from unloaded-state overwrites and adds
event-triggered rolling backups.

It does not add the proposed Web "compare cached hashes only" action. That is a
separate feature because it changes scan workflow and UI behavior rather than
database durability.

## Database Initialization

`ScanService` will expose one initialization operation used by `Program.cs`.
Initialization must:

1. Configure the database folder from the loaded Web settings.
2. Load the scan database.
3. Only after a successful load, restore the Web results snapshot.

The service will track whether the database was loaded successfully. Loading
will be idempotent and serialized so concurrent requests cannot load or replace
the process-wide database state at the same time.

Delete, move, and link operations must call the same load guard before changing
media files. If loading fails, the operation returns an error and does not
change media files, results, or the scan database.

## Backup Policy

Keep five rolling backups in the same directory as `ScannedFiles.db`.

Backups are event-triggered:

- once before a full scan starts changing the loaded database;
- once before each delete batch;
- once before each move batch;
- once before each hardlink or symlink batch.

Database checkpoints do not create backups. A large database may be written
many times during a scan, and backing up every checkpoint would add excessive
NAS I/O without materially improving recovery.

The newest backup is slot 1 and the oldest is slot 5:

- `ScannedFiles.backup-1.db`
- `ScannedFiles.backup-2.db`
- `ScannedFiles.backup-3.db`
- `ScannedFiles.backup-4.db`
- `ScannedFiles.backup-5.db`

Creating a backup rotates existing slots from oldest to newest, copies the
current main database to a temporary backup file, closes and flushes it, then
atomically moves it into slot 1. Rotation and database writes use one shared
database I/O lock.

If no main database exists, backup creation succeeds without creating an empty
backup. If a required backup fails, the full scan or destructive batch
operation is aborted before changing database or media state.

## Recovery Policy

Loading first follows the existing temporary/main database recovery behavior.
If neither candidate can be deserialized, loading tries backup slots 1 through
5 in order.

The first valid backup becomes the in-memory database and is copied back to
`ScannedFiles.db` through a temporary restore file and atomic move. Invalid
backup slots are logged and skipped; they are not overwritten during recovery.

If every candidate fails, loading reports failure. Web file operations remain
disabled for that request so an empty in-memory database cannot overwrite
recoverable files.

## Scan Behavior

A full scan must load the database before creating its one scan backup. The
backup therefore captures the last committed database, not a partially mutated
in-memory scan.

After the backup succeeds, file enumeration, metadata comparison, hashing,
checkpoints, and final save continue through the existing pipeline.

## Logging

Log:

- the configured database folder and loaded entry count;
- each backup creation and rotation failure;
- each failed main/temp/backup load attempt;
- automatic recovery source and restored entry count;
- refusal of a scan or Web file operation when loading or backup creation fails.

Logs must not include hash contents.

## Tests

Automated tests will cover:

1. Web initialization loads `ScannedFiles.db` before restoring results.
2. Deleting from restored results after restart preserves every unrelated
   database entry.
3. Move and link operations cannot save an unloaded database.
4. A load failure prevents media mutation.
5. Backup creation keeps exactly five versions and rotates them in order.
6. Backup creation is skipped cleanly when no main database exists.
7. A corrupt main database restores the newest valid backup.
8. A corrupt newest backup falls back to the next valid backup.
9. A failed required backup prevents the associated scan or batch operation.
10. Existing database format and Web result snapshot tests remain green.

## Success Criteria

- Restarting the Web application and deleting, moving, or linking restored
  results cannot replace a populated scan database with empty state.
- The five newest pre-operation/pre-scan database versions remain recoverable.
- A corrupt main database automatically restores from the newest valid backup.
- Failure to load or back up the database blocks destructive media operations.

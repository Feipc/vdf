# Database Safety and Backups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent an unloaded or corrupt scan database from being overwritten by Web operations and retain five recoverable pre-operation database versions.

**Architecture:** `DatabaseUtils` owns one process-wide I/O lock, durable backup rotation, candidate loading, and backup recovery. `ScanService` serializes database initialization, restores Web results only after a successful database load, and blocks scans and destructive batches unless load and backup both succeed.

**Tech Stack:** .NET 10, C#, MemoryPack, xUnit, Blazor Server.

---

### Task 1: Specify backup rotation and recovery

**Files:**
- Create: `VDF.Core.Tests/Utils/DatabaseSafetyTests.cs`
- Modify: `VDF.Core/Utils/DatabaseUtils.cs`
- Modify: `VDF.Core/ScanEngine.cs`

- [x] Write tests proving five-slot rotation, no-main skip, newest-valid recovery, fallback recovery, and backup failure.
- [ ] Run the focused Core tests and confirm they fail because backup APIs and recovery do not exist.
- [x] Add a shared database I/O lock and durable backup/restore primitives.
- [x] Replace recursive loading with ordered temp, main, then backup candidate loading.
- [x] Run the focused Core tests and existing database-format tests.

### Task 2: Specify Web initialization ordering

**Files:**
- Create: `VDF.Web.Tests/ScanServiceDatabaseSafetyTests.cs`
- Modify: `VDF.Web/Services/ScanService.cs`
- Modify: `VDF.Web/Program.cs`
- Modify: `VDF.Core/VDF.Core.csproj`

- [x] Write tests proving a valid database loads before results restore and a failed load prevents snapshot restore.
- [ ] Run the focused Web tests and confirm the new initialization API is missing.
- [x] Add serialized, idempotent initialization and loaded-folder tracking to `ScanService`.
- [x] Replace direct snapshot restoration in `Program.cs` with the initialization operation.
- [x] Run the focused Web tests.

### Task 3: Protect destructive Web batches

**Files:**
- Modify: `VDF.Web.Tests/ScanServiceDatabaseSafetyTests.cs`
- Modify: `VDF.Web/Services/ScanService.cs`

- [x] Write tests proving delete preserves unrelated entries and failed load/backup prevents delete, move, and link media mutations.
- [ ] Run the focused tests and confirm current operations mutate media or save unloaded state.
- [x] Add a common load-and-backup guard before every destructive batch.
- [x] Ensure guard failures return actionable errors without changing results or media.
- [x] Run the focused Web tests.

### Task 4: Protect full scans

**Files:**
- Modify: `VDF.Web.Tests/ScanServiceDatabaseSafetyTests.cs`
- Modify: `VDF.Web/Services/ScanService.cs`

- [x] Write a test proving a failed required backup leaves restored results intact and refuses scan startup.
- [ ] Run the focused test and confirm the scan currently advances.
- [x] Require successful initialization and one backup before clearing results or starting `ScanEngine`.
- [x] Run the focused Web tests.

### Task 5: Documentation and regression verification

**Files:**
- Modify: `README.md`

- [x] Document the five backup filenames, trigger events, and automatic recovery order.
- [x] Run all Core tests.
- [x] Run all Web tests.
- [x] Publish `VDF.Web` in Release mode.

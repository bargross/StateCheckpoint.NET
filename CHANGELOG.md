# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.1] — 2026-07-12

Patch release fixing all critical correctness bugs identified in the initial release. No public API surface changes.

### Fixed

- **`SqlServerStoreBase` thread-safety** — the store previously held a single shared `SqlConnection` instance, which is not thread-safe. Concurrent calls to any SQL Server store method would corrupt each other's state. The owned-connection path now creates a fresh `SqlConnection` per call (matching the connection pool pattern used by the Postgres store); externally supplied connections are guarded with a `SemaphoreSlim(1,1)`.

- **`PostgresModelStore.LoadAsync` transaction scope** — large object reads were happening outside the transaction that produced the OIDs. After `reader.CloseAsync()`, the metadata transaction was closed, leaving the OIDs as dangling references that could be unlinked by a concurrent writer before the read completed. The metadata read and both `ReadLargeObjectAsync` calls are now wrapped in a single transaction, passed explicitly through the call chain.

- **`FileSystemModelStore.ListAsync` and `FileSystemSessionStore.ListAsync` silently ignored tag filters** — calling `ListAsync(tagKey: "env", tagValue: "prod")` returned all checkpoints regardless of tags, with no error or warning. Both stores now load each `manifest.json` / `meta.json` in memory and filter by the requested tag key/value pair before returning results.

- **`BackgroundSaver.Enqueue` blocked the caller synchronously** — the previous implementation called `.GetAwaiter().GetResult()` on the channel `WriteAsync`, which blocked the calling thread whenever the bounded queue was full. This defeated the purpose of background saves and introduced deadlock risk on the thread pool. `Enqueue` has been replaced with `EnqueueAsync` returning `ValueTask`; both `CheckpointManager` and `SessionManager` now `await` it correctly.

- **Postgres connections were never returned to the pool** — `GetConnectionAsync` opened a connection from the `NpgsqlDataSource` pool but no call site disposed it, preventing connections from being returned. All call sites across `PostgresModelStore` and `PostgresSessionStore` now use `using var connection = await GetConnectionAsync(...)` to ensure prompt return to the pool.

- **SQL Server tag filtering produced incorrect results** — tag queries used `LIKE '%"key":"value"%'` string matching against the JSON column, which failed when the serializer emitted spaces and produced false positives when a value contained the search string as a substring. Replaced with `JSON_VALUE(Tags, '$.{key}') = @TagValue` for correct, indexed JSON field extraction (requires SQL Server 2016+).

- **`TokenizerData.MergeRules` did not serialise correctly** — the field was typed as `List<(string Left, string Right)>?` (C# value tuples), which `System.Text.Json` does not serialise by default, producing `{"Item1":"...","Item2":"..."}` or failing silently depending on runtime version. Changed to `List<MergeRule>?` using the existing `MergeRule` class, which serialises correctly in all versions.

- **SQL Server stores were in the wrong namespace** — `SqlServerStoreBase`, `SqlServerModelStore`, and `SqlServerSessionStore` were in `StateCheckpoint.NET.Stores.Mysql`. Corrected to `StateCheckpoint.NET.Stores` with files moved to `Stores/SqlServer/`.

- PostgresModelStore and PostgresSessionStore connections not disposed asynchronously — four call sites in PostgresModelStore and five in PostgresSessionStore used using var connection (synchronous IDisposable) instead of await using var connection (IAsyncDisposable). NpgsqlConnection implements IAsyncDisposable and disposing it synchronously blocks the thread pool while the connection is returned. All call sites now use await using.

## [1.0.0] - 2025-06-25

### Added
- Initial release of StateCheckpoint.NET.
- Support for saving/loading training checkpoints (weights + optimizer).
- Support for saving/loading inference sessions (KV-cache + token history).
- FileSystem, PostgreSQL, and SQL Server storage providers.
- Background (non‑blocking) saves with configurable queue.
- Complete tokenizer persistence (vocab maps, merge rules, special tokens).
- GUID‑based management with tagging system.
- Permission handling with fallback paths for file‑system stores.
- Full async/await support with cancellation tokens.
- Framework‑agnostic design – works with any C# ML library.

### Security
- Memory‑safe deep copying to prevent data corruption during background saves.
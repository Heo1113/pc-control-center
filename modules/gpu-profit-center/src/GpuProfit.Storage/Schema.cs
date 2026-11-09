namespace GpuProfit.Storage;

/// <summary>
/// Database schema. <c>PRAGMA user_version</c> holds the version; a newer database is never opened (data protection),
/// an older one is migrated forward inside one transaction. Money is stored as invariant decimal TEXT, never as float.
/// Times are Unix milliseconds (UTC).
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 1;

    /// <summary>Marker that identifies this app's database (an empty or foreign file is not ours).</summary>
    public const string AppId = "gpu-profit-center";

    public static readonly string[] RequiredTables =
    [
        "meta", "app_runs", "devices", "gpu_samples", "system_samples", "gaps", "workloads", "sessions", "session_segments",
        "revenue_entries", "cost_entries", "equipment", "exchange_rates", "session_results", "scenarios",
    ];

    public const string V1 = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS app_runs (
            id                 INTEGER PRIMARY KEY,
            started_ms         INTEGER NOT NULL,
            heartbeat_ms       INTEGER,
            ended_ms           INTEGER,
            end_reason         TEXT,
            app_version        TEXT NOT NULL,
            is_mock            INTEGER NOT NULL,
            sample_interval_ms INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS devices (
            id            INTEGER PRIMARY KEY,
            gpu_index     INTEGER NOT NULL,
            name          TEXT NOT NULL,
            is_mock       INTEGER NOT NULL DEFAULT 0,
            memory_total  INTEGER,
            power_limit_w REAL,
            first_seen_ms INTEGER NOT NULL,
            last_seen_ms  INTEGER NOT NULL,
            UNIQUE (gpu_index, name, is_mock)
        );

        -- One row per GPU per sample time; the primary key makes a repeated write a no-op (no duplicate records).
        CREATE TABLE IF NOT EXISTS gpu_samples (
            device_id  INTEGER NOT NULL REFERENCES devices(id),
            t_ms       INTEGER NOT NULL,
            run_id     INTEGER NOT NULL,
            power_w    REAL,
            util       REAL,
            mem_util   REAL,
            temp_c     REAL,
            vram_used  INTEGER,
            vram_total INTEGER,
            clk_gfx    REAL,
            clk_mem    REAL,
            energy_mj  INTEGER,
            PRIMARY KEY (device_id, t_ms)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_gpu_samples_t ON gpu_samples (t_ms);
        CREATE INDEX IF NOT EXISTS ix_gpu_samples_run ON gpu_samples (run_id);

        CREATE TABLE IF NOT EXISTS system_samples (
            t_ms      INTEGER PRIMARY KEY,
            run_id    INTEGER NOT NULL,
            is_mock   INTEGER NOT NULL DEFAULT 0,
            cpu       REAL,
            ram_used  INTEGER,
            ram_total INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_system_samples_run ON system_samples (run_id);

        CREATE TABLE IF NOT EXISTS gaps (
            id          INTEGER PRIMARY KEY,
            scope       TEXT NOT NULL,
            kind        TEXT NOT NULL,
            from_ms     INTEGER,
            to_ms       INTEGER,
            detected_ms INTEGER NOT NULL,
            detail      TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_gaps_from ON gaps (from_ms);

        CREATE TABLE IF NOT EXISTS workloads (
            id         INTEGER PRIMARY KEY,
            name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
            type       INTEGER NOT NULL,
            notes      TEXT,
            archived   INTEGER NOT NULL DEFAULT 0,
            created_ms INTEGER NOT NULL,
            updated_ms INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id                  INTEGER PRIMARY KEY,
            workload_id         INTEGER NOT NULL REFERENCES workloads(id),
            title               TEXT,
            notes               TEXT,
            state               INTEGER NOT NULL,
            source              INTEGER NOT NULL,
            started_ms          INTEGER NOT NULL,
            ended_ms            INTEGER,
            end_reason          TEXT,
            run_id              INTEGER,
            is_mock             INTEGER NOT NULL DEFAULT 0,
            devices             TEXT,
            execution_confirmed INTEGER NOT NULL DEFAULT 0,
            no_revenue          INTEGER NOT NULL DEFAULT 0,
            energy_basis        INTEGER,
            extra_system_w      REAL,
            idle_gpu_w          REAL,
            unobserved_policy   INTEGER,
            rate_krw_kwh        TEXT,
            wall_meter_kwh      REAL,
            work_hours          REAL,
            created_ms          INTEGER NOT NULL,
            updated_ms          INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_started ON sessions (started_ms);
        CREATE INDEX IF NOT EXISTS ix_sessions_workload ON sessions (workload_id);

        CREATE TABLE IF NOT EXISTS session_segments (
            id         INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER,
            end_reason TEXT,
            run_id     INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_segments_session ON session_segments (session_id);
        CREATE INDEX IF NOT EXISTS ix_segments_range ON session_segments (start_ms, end_ms);

        CREATE TABLE IF NOT EXISTS revenue_entries (
            id           INTEGER PRIMARY KEY,
            session_id   INTEGER REFERENCES sessions(id) ON DELETE CASCADE,
            workload_id  INTEGER REFERENCES workloads(id),
            kind         INTEGER NOT NULL,
            amount       TEXT NOT NULL,
            currency     TEXT NOT NULL,
            is_gross     INTEGER NOT NULL DEFAULT 0,
            occurred_ms  INTEGER NOT NULL,
            provider     TEXT,
            external_id  TEXT,
            note         TEXT,
            origin       INTEGER NOT NULL,
            krw_per_unit TEXT,
            rate_as_of_ms INTEGER,
            rate_source  TEXT,
            created_ms   INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_revenue_session ON revenue_entries (session_id);
        -- An imported payout (provider + its id) is stored once.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_revenue_external ON revenue_entries (provider, external_id) WHERE external_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS cost_entries (
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER REFERENCES sessions(id) ON DELETE CASCADE,
            workload_id INTEGER REFERENCES workloads(id),
            category    INTEGER NOT NULL,
            basis       INTEGER NOT NULL,
            amount      TEXT NOT NULL,
            currency    TEXT NOT NULL,
            occurred_ms INTEGER NOT NULL,
            note        TEXT,
            active      INTEGER NOT NULL DEFAULT 1,
            origin      INTEGER NOT NULL,
            created_ms  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS equipment (
            id           INTEGER PRIMARY KEY,
            name         TEXT NOT NULL,
            price_krw    TEXT NOT NULL,
            salvage_krw  TEXT NOT NULL,
            purchased_ms INTEGER NOT NULL,
            method       INTEGER NOT NULL,
            life_months  REAL NOT NULL,
            life_hours   REAL,
            workload_id  INTEGER REFERENCES workloads(id),
            note         TEXT,
            active       INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS exchange_rates (
            id           INTEGER PRIMARY KEY,
            currency     TEXT NOT NULL,
            krw_per_unit TEXT NOT NULL,
            as_of_ms     INTEGER NOT NULL,
            source       TEXT,
            origin       INTEGER NOT NULL,
            note         TEXT,
            UNIQUE (currency, as_of_ms, source)
        );

        -- Every calculation that was saved, with its conditions: the history of a session's results.
        CREATE TABLE IF NOT EXISTS session_results (
            id              INTEGER PRIMARY KEY,
            session_id      INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            computed_ms     INTEGER NOT NULL,
            engine_version  INTEGER NOT NULL,
            inputs_hash     TEXT NOT NULL,
            reason          TEXT NOT NULL,
            summary_json    TEXT NOT NULL,
            conditions_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_results_session ON session_results (session_id, computed_ms);

        CREATE TABLE IF NOT EXISTS scenarios (
            id               INTEGER PRIMARY KEY,
            name             TEXT NOT NULL,
            kind             INTEGER NOT NULL,
            params_json      TEXT NOT NULL,
            source_session_id INTEGER,
            source_hash      TEXT,
            notes            TEXT,
            last_result_json TEXT,
            created_ms       INTEGER NOT NULL,
            updated_ms       INTEGER NOT NULL
        );
        """;
}

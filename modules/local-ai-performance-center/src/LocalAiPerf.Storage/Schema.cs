namespace LocalAiPerf.Storage;

/// <summary>Database schema. Times are Unix milliseconds (UTC). No prompt or answer text is ever stored.</summary>
internal static class Schema
{
    /// <summary>
    /// 2: a build of this app (commit dc85d73, since removed) added a benchmark_runs table. Version 2 adds nothing new, so
    /// databases that build already upgraded stay readable; an existing benchmark_runs table is left untouched.
    /// </summary>
    public const int CurrentVersion = 2;

    public static readonly string[] RequiredTables =
        ["sessions", "system_samples", "gpu_samples", "runtime_samples", "runtime_requests", "runtime_info", "events"];

    public const string V1 = """
        CREATE TABLE IF NOT EXISTS sessions (
            id INTEGER PRIMARY KEY,
            uid TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            notes TEXT,
            tags TEXT,
            kind INTEGER NOT NULL,
            started_ms INTEGER NOT NULL,
            ended_ms INTEGER,
            end_reason TEXT,
            last_sample_ms INTEGER,
            sample_interval_ms INTEGER NOT NULL,
            app_version TEXT NOT NULL,
            host_json TEXT,
            runtime_kind TEXT,
            runtime_endpoint TEXT,
            sample_count INTEGER NOT NULL DEFAULT 0,
            is_mock INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_started ON sessions(started_ms);

        CREATE TABLE IF NOT EXISTS system_samples (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            t_ms INTEGER NOT NULL,
            cpu_util REAL,
            ram_used INTEGER,
            ram_total INTEGER,
            flags INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (session_id, t_ms)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS gpu_samples (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            t_ms INTEGER NOT NULL,
            gpu_index INTEGER NOT NULL,
            util REAL,
            mem_util REAL,
            temp_c REAL,
            power_w REAL,
            power_limit_w REAL,
            vram_used INTEGER,
            vram_total INTEGER,
            clock_gfx_mhz REAL,
            clock_mem_mhz REAL,
            fan_pct REAL,
            pcie_tx_kbps REAL,
            pcie_rx_kbps REAL,
            energy_mj INTEGER,
            throttle INTEGER,
            PRIMARY KEY (session_id, t_ms, gpu_index)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS runtime_samples (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            t_ms INTEGER NOT NULL,
            state INTEGER NOT NULL,
            error_category INTEGER,
            model_loaded INTEGER,
            answer_tok_s REAL,
            prefill_tok_s REAL,
            aggregate_tok_s REAL,
            active INTEGER,
            queued INTEGER,
            waiting INTEGER,
            occupied_slots INTEGER,
            slots_effective INTEGER,
            context_max INTEGER,
            context_used INTEGER,
            output_counter INTEGER,
            admission_rejected INTEGER,
            queue_timeouts INTEGER,
            disconnect_cancels INTEGER,
            PRIMARY KEY (session_id, t_ms)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS runtime_requests (
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            instance_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            observed_ms INTEGER NOT NULL,
            started_ms INTEGER,
            duration_s REAL,
            finish TEXT,
            prompt_tokens INTEGER,
            reused_tokens INTEGER,
            output_tokens INTEGER,
            prompt_ms REAL,
            decode_ms REAL,
            first_token_s REAL,
            PRIMARY KEY (session_id, instance_id, request_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS runtime_info (
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            t_ms INTEGER NOT NULL,
            runtime_kind TEXT,
            service TEXT,
            engine_version TEXT,
            instance_id TEXT,
            model_id TEXT,
            model_file TEXT,
            context_max INTEGER,
            slots INTEGER,
            settings_json TEXT,
            fingerprint TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_runtime_info_session ON runtime_info(session_id, t_ms);

        CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY,
            session_id INTEGER REFERENCES sessions(id) ON DELETE CASCADE,
            t_ms INTEGER NOT NULL,
            severity INTEGER NOT NULL,
            category TEXT NOT NULL,
            message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_session ON events(session_id, t_ms);
        CREATE INDEX IF NOT EXISTS ix_events_time ON events(t_ms);
        """;
}

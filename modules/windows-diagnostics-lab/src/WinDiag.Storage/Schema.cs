namespace WinDiag.Storage;

/// <summary>
/// Database schema. Times are Unix milliseconds (UTC). The original event XML is never stored and snapshots do not read
/// serial numbers. User names, computer names and SIDs are masked before storage unless the user turns masking off.
/// Device instance IDs in event data are kept as written (a USB one can contain the device's serial number); minimal
/// exports shorten them.
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 1;

    public static readonly string[] RequiredTables =
        ["meta", "events", "channel_state", "gaps", "runs", "sensor_samples", "gpu_samples", "snapshots", "user_records", "baselines", "comparisons", "notes", "dump_files"];

    public const string V1 = """
        CREATE TABLE IF NOT EXISTS meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY,
            channel TEXT NOT NULL,
            provider TEXT NOT NULL,
            event_source TEXT,
            event_id INTEGER NOT NULL,
            qualifiers INTEGER,
            level INTEGER NOT NULL,
            record_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            ingested_ms INTEGER NOT NULL,
            category TEXT NOT NULL,
            severity INTEGER NOT NULL,
            group_key TEXT NOT NULL,
            signature TEXT NOT NULL,
            title TEXT NOT NULL,
            message TEXT,
            message_error TEXT,
            data_json TEXT,
            process_id INTEGER,
            thread_id INTEGER,
            source TEXT NOT NULL,
            is_masked INTEGER NOT NULL DEFAULT 1,
            UNIQUE (channel, record_id, provider, event_id, time_ms)
        );
        CREATE INDEX IF NOT EXISTS ix_events_time ON events(time_ms);
        CREATE INDEX IF NOT EXISTS ix_events_group ON events(group_key, time_ms);
        CREATE INDEX IF NOT EXISTS ix_events_category ON events(category, time_ms);
        CREATE INDEX IF NOT EXISTS ix_events_provider ON events(provider, event_id);

        CREATE TABLE IF NOT EXISTS channel_state (
            channel TEXT PRIMARY KEY,
            status INTEGER NOT NULL,
            status_message TEXT,
            last_record_id INTEGER,
            last_scan_ms INTEGER,
            last_event_ms INTEGER,
            oldest_record_id INTEGER,
            oldest_event_ms INTEGER,
            record_count INTEGER,
            history_from_ms INTEGER
        );

        CREATE TABLE IF NOT EXISTS gaps (
            id INTEGER PRIMARY KEY,
            scope TEXT NOT NULL,
            kind TEXT NOT NULL,
            from_ms INTEGER,
            to_ms INTEGER,
            detected_ms INTEGER NOT NULL,
            detail TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_gaps_time ON gaps(detected_ms);

        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY,
            started_ms INTEGER NOT NULL,
            ended_ms INTEGER,
            end_reason TEXT,
            heartbeat_ms INTEGER,
            app_version TEXT NOT NULL,
            is_mock INTEGER NOT NULL DEFAULT 0,
            sensor_interval_ms INTEGER NOT NULL,
            elevated INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS sensor_samples (
            t_ms INTEGER NOT NULL PRIMARY KEY,
            run_id INTEGER NOT NULL,
            cpu_util REAL,
            ram_used INTEGER,
            ram_total INTEGER,
            flags INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS gpu_samples (
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
            throttle INTEGER,
            PRIMARY KEY (t_ms, gpu_index)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS snapshots (
            id INTEGER PRIMARY KEY,
            first_seen_ms INTEGER NOT NULL,
            last_confirmed_ms INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            source TEXT NOT NULL,
            is_mock INTEGER NOT NULL DEFAULT 0,
            json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_snapshots_time ON snapshots(first_seen_ms);

        CREATE TABLE IF NOT EXISTS user_records (
            id INTEGER PRIMARY KEY,
            time_ms INTEGER NOT NULL,
            created_ms INTEGER NOT NULL,
            kind TEXT NOT NULL,
            title TEXT NOT NULL,
            detail TEXT,
            values_json TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_user_records_time ON user_records(time_ms);

        CREATE TABLE IF NOT EXISTS baselines (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            time_ms INTEGER NOT NULL,
            created_ms INTEGER NOT NULL,
            description TEXT,
            user_record_id INTEGER REFERENCES user_records(id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS comparisons (
            id INTEGER PRIMARY KEY,
            baseline_id INTEGER REFERENCES baselines(id) ON DELETE CASCADE,
            created_ms INTEGER NOT NULL,
            title TEXT NOT NULL,
            result_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notes (
            id INTEGER PRIMARY KEY,
            target_kind TEXT NOT NULL,
            target_key TEXT NOT NULL,
            created_ms INTEGER NOT NULL,
            updated_ms INTEGER NOT NULL,
            text TEXT NOT NULL,
            UNIQUE (target_kind, target_key)
        );

        CREATE TABLE IF NOT EXISTS dump_files (
            path TEXT PRIMARY KEY,
            size INTEGER,
            write_ms INTEGER,
            status TEXT NOT NULL,
            status_message TEXT,
            signature TEXT,
            bugcheck INTEGER,
            params TEXT,
            dump_ms INTEGER,
            dump_type INTEGER,
            processors INTEGER,
            build INTEGER,
            seen_ms INTEGER NOT NULL
        );
        """;
}

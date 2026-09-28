using System.IO;
using Microsoft.Data.Sqlite;

namespace FileMemo.App.Data;

/// <summary>
/// 本地 SQLite 主库 + FTS5 全文索引（需求 3.12 / 5）。
/// 采用手写建表，保证在不同 SQLite 运行时上行为一致，便于离线部署。
/// </summary>
public sealed partial class Database : IDisposable
{
    public string DbPath { get; }
    private readonly SqliteConnection _anchor;

    public Database(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _anchor = new SqliteConnection($"Data Source={dbPath}");
        _anchor.Open();
        using var pragma = _anchor.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
    }

    /// <summary>打开一个短生命周期连接（WAL 下可并发读）。</summary>
    public SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        ApplyP1Migrations(c);
        ApplyP2Migrations(c);
        EnsureFts(c);
    }

    private static void EnsureFts(SqliteConnection c)
    {
        // FTS5 可用则建立全文索引，不可用则跳过（降级为 LIKE 搜索）。
        try
        {
            using var probe = c.CreateCommand();
            probe.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS record_fts USING fts5(" +
                                "record_id UNINDEXED, title, content, tags, tokenize='unicode61');";
            probe.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 当前 SQLite 未编译 FTS5：Repository 会自动降级。
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS note_record (
            id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL,
            title TEXT,
            content_md TEXT,
            content_html TEXT,
            color TEXT,
            tags TEXT,
            pinned INTEGER DEFAULT 0,
            created_at TEXT,
            updated_at TEXT,
            file_ref_id TEXT
        );

        CREATE TABLE IF NOT EXISTS clip (
            id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL,
            content TEXT,
            image_path TEXT,
            source_app TEXT,
            created_at TEXT,
            pinned INTEGER DEFAULT 0,
            expire_at TEXT,
            content_hash TEXT
        );

        CREATE TABLE IF NOT EXISTS task (
            id TEXT PRIMARY KEY,
            title TEXT,
            description TEXT,
            state INTEGER,
            done INTEGER DEFAULT 0,
            priority INTEGER,
            due_at TEXT,
            repeat_rule TEXT,
            progress INTEGER,
            owner TEXT,
            tags TEXT,
            file_ref_id TEXT,
            linked_record_id TEXT,
            created_at TEXT
        );

        CREATE TABLE IF NOT EXISTS file_ref (
            id TEXT PRIMARY KEY,
            volume_guid TEXT,
            file_id INTEGER,
            usn INTEGER,
            path TEXT,
            size INTEGER,
            mtime TEXT,
            ctime TEXT,
            quick_hash TEXT,
            full_hash TEXT,
            parent_file_id INTEGER,
            ext TEXT,
            is_dir INTEGER,
            offline INTEGER DEFAULT 0,
            last_seen TEXT
        );

        CREATE TABLE IF NOT EXISTS annotation (
            id TEXT PRIMARY KEY,
            file_ref_id TEXT,
            content TEXT,
            images_json TEXT,
            tags TEXT,
            state INTEGER,
            intent TEXT,
            source TEXT,
            owner TEXT,
            due_at TEXT,
            created_at TEXT,
            updated_at TEXT,
            version_chain TEXT
        );

        CREATE TABLE IF NOT EXISTS fingerprint (
            id TEXT PRIMARY KEY,
            file_ref_id TEXT,
            type INTEGER,
            value TEXT,
            confidence REAL
        );

        CREATE TABLE IF NOT EXISTS timeline (
            id TEXT PRIMARY KEY,
            object_type TEXT,
            object_id TEXT,
            action TEXT,
            detail TEXT,
            created_at TEXT
        );

        CREATE TABLE IF NOT EXISTS record_link (
            id TEXT PRIMARY KEY,
            from_record_id TEXT,
            to_record_id TEXT,
            link_type TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_note_kind ON note_record(kind);
        CREATE INDEX IF NOT EXISTS ix_note_fileref ON note_record(file_ref_id);
        CREATE INDEX IF NOT EXISTS ix_clip_time ON clip(created_at);
        CREATE INDEX IF NOT EXISTS ix_task_fileref ON task(file_ref_id);
        CREATE INDEX IF NOT EXISTS ix_anno_fileref ON annotation(file_ref_id);
        CREATE INDEX IF NOT EXISTS ix_fpr_fileref ON fingerprint(file_ref_id);
        CREATE INDEX IF NOT EXISTS ix_link_from ON record_link(from_record_id);
        CREATE INDEX IF NOT EXISTS ix_link_to ON record_link(to_record_id);
        """;

    public void Dispose() => _anchor.Dispose();
}

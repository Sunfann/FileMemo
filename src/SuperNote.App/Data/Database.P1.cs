using Microsoft.Data.Sqlite;

namespace SuperNote.App.Data;

/// <summary>
/// P1 数据库迁移（需求 3.5 / 3.7 / 3.12）：
///   - file_ref 增列 volume_serial / is_network（网络盘 / NAS 辅助指纹）
///   - clip 增列 ocr_text（图片 OCR 参与搜索）
///   - annotation 增列 ocr_text（备注插图 OCR 参与搜索）
///   - 新增 sidecar 表（伴生文件登记）
///   - 新增 version_link 表（版本关系链）
/// SQLite 的 ADD COLUMN 无 IF NOT EXISTS，故逐条 try/catch 幂等执行。
/// </summary>
public sealed partial class Database
{
    private static void ApplyP1Migrations(SqliteConnection c)
    {
        void Try(string sql)
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* 列已存在 / 表已存在：忽略 */ }
        }

        // 新列
        Try("ALTER TABLE file_ref ADD COLUMN volume_serial INTEGER;");
        Try("ALTER TABLE file_ref ADD COLUMN is_network INTEGER DEFAULT 0;");
        Try("ALTER TABLE clip ADD COLUMN ocr_text TEXT;");
        Try("ALTER TABLE annotation ADD COLUMN ocr_text TEXT;");

        // sidecar 表
        Try("""
            CREATE TABLE IF NOT EXISTS sidecar (
                id TEXT PRIMARY KEY,
                file_ref_id TEXT,
                path TEXT,
                format INTEGER,
                last_synced_at TEXT,
                policy_flags TEXT
            );
            """);
        Try("CREATE INDEX IF NOT EXISTS ix_sidecar_fileref ON sidecar(file_ref_id);");

        // 版本关系链表
        Try("""
            CREATE TABLE IF NOT EXISTS version_link (
                id TEXT PRIMARY KEY,
                from_file_ref_id TEXT,
                to_file_ref_id TEXT,
                version INTEGER,
                note TEXT,
                created_at TEXT
            );
            """);
        Try("CREATE INDEX IF NOT EXISTS ix_version_from ON version_link(from_file_ref_id);");
        Try("CREATE INDEX IF NOT EXISTS ix_version_to ON version_link(to_file_ref_id);");
    }
}

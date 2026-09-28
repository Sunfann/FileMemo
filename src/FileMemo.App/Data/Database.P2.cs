using Microsoft.Data.Sqlite;

namespace FileMemo.App.Data;

/// <summary>
/// P2 数据库迁移：
///   - image_vector：本地图像特征向量（图片向量搜索）
///   - plugin：插件注册表（插件系统）
///   - collab_session / collab_member：团队协作预留
/// 幂等执行（IF NOT EXISTS / try-catch），由 Migrate() 调用。
/// </summary>
public sealed partial class Database
{
    private static void ApplyP2Migrations(SqliteConnection c)
    {
        void Try(string sql)
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* 表/列已存在：忽略 */ }
        }

        // 图片向量表
        Try("""
            CREATE TABLE IF NOT EXISTS image_vector (
                id TEXT PRIMARY KEY,
                file_ref_id TEXT,
                image_path TEXT,
                dim INTEGER,
                vector TEXT,
                created_at TEXT
            );
            """);
        Try("CREATE INDEX IF NOT EXISTS ix_vector_fileref ON image_vector(file_ref_id);");
        Try("CREATE INDEX IF NOT EXISTS ix_vector_path ON image_vector(image_path);");

        // 插件表
        Try("""
            CREATE TABLE IF NOT EXISTS plugin (
                id TEXT PRIMARY KEY,
                name TEXT,
                version TEXT,
                author TEXT,
                description TEXT,
                assembly_path TEXT,
                type_name TEXT,
                state INTEGER,
                enabled INTEGER,
                error TEXT
            );
            """);

        // 协作会话表
        Try("""
            CREATE TABLE IF NOT EXISTS collab_session (
                id TEXT PRIMARY KEY,
                name TEXT,
                workspace_id TEXT,
                provider TEXT,
                owner TEXT,
                created_at TEXT,
                updated_at TEXT,
                members_json TEXT,
                sync_state TEXT
            );
            """);
    }
}

using Microsoft.Data.Sqlite;

namespace FileMemo.App.Data;

/// <summary>
/// 待办提醒迁移：为 task 表增加提醒时间（remind_at）与已提醒标记（reminded）。
/// SQLite 的 ADD COLUMN 无 IF NOT EXISTS，故逐条 try/catch 幂等执行。
/// </summary>
public sealed partial class Database
{
    private static void ApplyReminderMigrations(SqliteConnection c)
    {
        void Try(string sql)
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* 列已存在 / 索引已存在：忽略 */ }
        }

        Try("ALTER TABLE task ADD COLUMN remind_at TEXT;");
        Try("ALTER TABLE task ADD COLUMN reminded INTEGER DEFAULT 0;");
        Try("CREATE INDEX IF NOT EXISTS ix_task_remind ON task(remind_at);");
    }
}

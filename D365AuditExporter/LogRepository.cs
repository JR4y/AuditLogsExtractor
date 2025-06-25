using LiteDB;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace D365AuditExporter
{
    public class LogRepository : IDisposable
    {
        private readonly string _sqlitePath;
        private readonly SQLiteConnection _connection;
        private readonly object _lock = new object();

        public LogRepository(string sqlitePath)
        {
            _sqlitePath = sqlitePath;
            _connection = new SQLiteConnection($"Data Source={_sqlitePath};Version=3;");
            _connection.Open();
        }

        #region Read Operations
        public DateTime? GetLastExportedDate(string entityName, string recordId)
        {
            lock (_lock)
            {
                try
                {
                    string tableName = $"log_{entityName}";

                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT last_exported_date FROM {tableName} WHERE id = @id;";
                        cmd.Parameters.AddWithValue("@id", recordId);

                        var result = cmd.ExecuteScalar();
                        if (result != null && DateTime.TryParse(result.ToString(), out var parsedDate))
                        {
                            return parsedDate;
                        }

                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error retrieving last exported date for '{recordId}' in '{entityName}': {ex.Message}", "ERROR");
                    return null;
                }
            }
        }

        public IEnumerable<string> GetRecordIdsByStatus(string entityName, string status)
        {
            var table = $"log_{entityName}";
            var ids = new List<string>();

            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT id FROM {table} WHERE status = @status;";
                        cmd.Parameters.AddWithValue("@status", status);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ids.Add(reader.GetString(0));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error retrieving records by status in {table}: {ex.Message}", "ERROR");
                }
            }

            return ids;
        }

        public IEnumerable<(string Entity, string RecordId, DateTime Date)> GetUploadErrors()
        {
            var results = new List<(string Entity, string RecordId, DateTime Date)>();

            lock (_lock)
            {
                try
                {
                    using (var tablesCmd = _connection.CreateCommand())
                    {
                        tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'log_%';";

                        using (var reader = tablesCmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string tableName = reader.GetString(0);
                                string entity = tableName.Substring("log_".Length);

                                using (var selectCmd = _connection.CreateCommand())
                                {
                                    selectCmd.CommandText = $@"
                                        SELECT id, last_exported_date FROM {tableName}
                                        WHERE status = @status1 OR status = @status2;";
                                    selectCmd.Parameters.AddWithValue("@status1", ExportStatus.Error);
                                    selectCmd.Parameters.AddWithValue("@status2", ExportStatus.Retry);

                                    using (var result = selectCmd.ExecuteReader())
                                    {
                                        while (result.Read())
                                        {
                                            string id = result.GetString(0);
                                            string dateStr = result.GetString(1);
                                            if (DateTime.TryParse(dateStr, out var date))
                                            {
                                                results.Add((entity, id, date));
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error retrieving upload errors: {ex.Message}", "ERROR");
                }
            }
            return results;
        }
        #endregion

        #region Write Operations
        public void UpdateRecordStatus(string entityName, string id, DateTime date, string status)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        string table = $"log_{entityName}";
                        cmd.CommandText = $@"
                            INSERT OR REPLACE INTO {table} (id, last_exported_date, status)
                            VALUES (@id, @date, @status);";
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@date", date.ToUniversalTime().ToString("s"));
                        cmd.Parameters.AddWithValue("@status", status);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error updating export status for {entityName} > {id}: {ex.Message}", "ERROR");
                }
            }
        }
        #endregion

        #region Execution Summary
        public int InsertSummaryHeader(ExecutionSummaryHeader summary)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    INSERT INTO summary (start_time, mode, total_entities, status, notes, pause_requested)
                    VALUES (@start_time, @mode, @total_entities, @status, @notes, @pause_requested);
                    SELECT last_insert_rowid();";

                        cmd.Parameters.AddWithValue("@start_time", summary.StartTime.ToUniversalTime().ToString("s"));
                        cmd.Parameters.AddWithValue("@mode", summary.Mode);
                        cmd.Parameters.AddWithValue("@total_entities", summary.TotalEntities);
                        cmd.Parameters.AddWithValue("@status", summary.Status ?? "Running");
                        cmd.Parameters.AddWithValue("@notes", summary.Notes ?? "");
                        cmd.Parameters.AddWithValue("@pause_requested", 0);

                        return Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error inserting summary header: {ex.Message}", "ERROR");
                    return -1;
                }
            }
        }

        public void InsertDetailForEntity(int summaryId, string entity)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    INSERT INTO summary_detail (summary_id, entity, total, exported, errors, skipped, status, duration)
                    VALUES (@summary_id, @entity, 0, 0, 0, 0, 'Pending', '00:00');";

                        cmd.Parameters.AddWithValue("@summary_id", summaryId);
                        cmd.Parameters.AddWithValue("@entity", entity);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error inserting summary detail for entity '{entity}': {ex.Message}", "ERROR");
                }
            }
        }

        public void UpdateDetailProgress(int summaryId, string entity, int exported, int errors, int skipped)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    UPDATE summary_detail
                    SET exported = @exported,
                        errors = @errors,
                        skipped = @skipped
                    WHERE summary_id = @summary_id AND entity = @entity;";

                        cmd.Parameters.AddWithValue("@summary_id", summaryId);
                        cmd.Parameters.AddWithValue("@entity", entity);
                        cmd.Parameters.AddWithValue("@exported", exported);
                        cmd.Parameters.AddWithValue("@errors", errors);
                        cmd.Parameters.AddWithValue("@skipped", skipped);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error updating progress for entity '{entity}': {ex.Message}", "ERROR");
                }
            }
        }

        public void UpdateDetailStatus(int summaryId, string entity, string status, string duration)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    UPDATE summary_detail
                    SET status = @status,
                        duration = @duration
                    WHERE summary_id = @summary_id AND entity = @entity;";

                        cmd.Parameters.AddWithValue("@summary_id", summaryId);
                        cmd.Parameters.AddWithValue("@entity", entity);
                        cmd.Parameters.AddWithValue("@status", status);
                        cmd.Parameters.AddWithValue("@duration", duration);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error updating status for entity '{entity}': {ex.Message}", "ERROR");
                }
            }
        }

        public void FinalizeSummary(int summaryId, string finalStatus, string notes = null)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    UPDATE summary
                    SET end_time = @end_time,
                        status = @status,
                        notes = @notes
                    WHERE id = @id;";

                        cmd.Parameters.AddWithValue("@end_time", DateTime.UtcNow.ToString("s"));
                        cmd.Parameters.AddWithValue("@status", finalStatus);
                        cmd.Parameters.AddWithValue("@notes", notes ?? "");
                        cmd.Parameters.AddWithValue("@id", summaryId);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error finalizing summary ID {summaryId}: {ex.Message}", "ERROR");
                }
            }
        }
        #endregion

        #region Pause management
        public bool CheckPauseRequested(int summaryId)
        {
            lock (_lock)
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                    SELECT pause_requested FROM summary
                    WHERE id = @id;";
                        cmd.Parameters.AddWithValue("@id", summaryId);

                        var result = cmd.ExecuteScalar();
                        if (result != null && Convert.ToInt32(result) == 1)
                        {
                            Logger.Log("⏸️  Pausa solicitada detectada en la base de datos.", "INFO");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Error verificando instrucción de pausa: {ex.Message}", "ERROR");
                }

                return false;
            }
        }
        #endregion

        #region File Handling (SharePoint support)
        public static LogRepository LoadOrCreate(string dbFileName = "bitacora.sqlite")
        {
            // TODO: Implementar descarga o creación local
            throw new NotImplementedException();
        }

        public static void UploadDatabaseAndBackup(string fileName, string backupName)
        {
            // TODO: Implementar subida a SharePoint
            throw new NotImplementedException();
        }
        #endregion

        #region Cleanup
        public void Dispose()
        {
            lock (_lock)
            {
                _connection?.Dispose();
            }
        }
        #endregion

        #region Models
        public class ExecutionSummaryHeader
        {
            public int Id { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string Mode { get; set; }
            public int TotalEntities { get; set; }
            public int ProcessedEntities { get; set; }
            public string Status { get; set; }
            public string Notes { get; set; }
        }

        public class ExecutionSummaryDetail
        {
            public int Id { get; set; }
            public int SummaryId { get; set; }
            public string Entity { get; set; }
            public int Total { get; set; }
            public int Exported { get; set; }
            public int Errors { get; set; }
            public int Skipped { get; set; }
            public string Status { get; set; }
            public string Duration { get; set; }
        }

        public static class ExportStatus
        {
            public const string Done = "done";
            public const string Empty = "empty";
            public const string Error = "error";
            public const string Retry = "retry";
            public const string Deleted = "deleted";
        }
        #endregion

    }
}


/*
 * 
 * CREATE TABLE IF NOT EXISTS summary (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    start_time TEXT NOT NULL,
    end_time TEXT,
    mode TEXT NOT NULL,
    total_entities INTEGER NOT NULL,
    status TEXT NOT NULL,
    notes TEXT,
    pause_requested INTEGER DEFAULT 0 -- 0 = No, 1 = Sí
);

CREATE TABLE IF NOT EXISTS summary_detail (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    summary_id INTEGER NOT NULL,              -- FK a la tabla summary
    entity_name TEXT NOT NULL,
    total_records INTEGER NOT NULL,
    exported INTEGER NOT NULL DEFAULT 0,
    errors INTEGER NOT NULL DEFAULT 0,
    skipped INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL,                     -- "in_progress", "completed", "error"
    FOREIGN KEY (summary_id) REFERENCES summary(id)
);

*/

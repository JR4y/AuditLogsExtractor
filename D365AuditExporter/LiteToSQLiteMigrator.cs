using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using LiteDB;
using System.Linq;

public class LiteToSQLiteMigrator
{
    private readonly string _liteDbPath;
    private readonly string _sqlitePath;

    public LiteToSQLiteMigrator(string liteDbPath, string sqlitePath)
    {
        _liteDbPath = liteDbPath;
        _sqlitePath = sqlitePath;
    }

    public void EjecutarMigracion()
    {
        using (var liteDb = new LiteDatabase(_liteDbPath))
        using (var sqlite = new SQLiteConnection($"Data Source={_sqlitePath};Version=3;"))
        {
            sqlite.Open();
            string[] tablasPermitidas = { "bitacora_contact", "bitacora_account" }; // ← Las que quieres probar


            foreach (var colName in liteDb.GetCollectionNames())
            {
                if (!colName.StartsWith("bitacora_"))
                    continue;


                if (!tablasPermitidas.Contains(colName))
                    continue;

                var entidad = colName.Substring("bitacora_".Length);
                var tablaDestino = $"log_{entidad}";

                CrearTablaSiNoExiste(sqlite, tablaDestino);
                MigrarDatosDeColeccion(liteDb, sqlite, colName, tablaDestino);
            }

            Console.WriteLine("✅ Migración finalizada.");
        }
    }

    private void CrearTablaSiNoExiste(SQLiteConnection sqlite, string tabla)
    {
        var cmd = sqlite.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {tabla} (
                id TEXT PRIMARY KEY,
                last_exported_date TEXT NOT NULL,
                status TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_{tabla}_status ON {tabla}(status);
            CREATE INDEX IF NOT EXISTS idx_{tabla}_date ON {tabla}(last_exported_date);
        ";
        cmd.ExecuteNonQuery();
    }

    private void MigrarDatosDeColeccion(LiteDatabase liteDb, SQLiteConnection sqlite, string colName, string tabla)
    {
        var coleccion = liteDb.GetCollection(colName);
        int contador = 0;

        foreach (var doc in coleccion.FindAll())
        {
            //string id = doc["_id"].ToString().Replace("\0", "").Trim();
            //string id = doc["_id"].AsGuid.ToString();
            string id = doc["_id"].RawValue.ToString();
            DateTime fecha = doc["UltimaFechaExportada"].AsDateTime;
            string estadoLiteDb = doc["Estado"].AsString;
            string estadoSqlite = TraducirEstado(estadoLiteDb);

            var cmd = sqlite.CreateCommand();
            cmd.CommandText = $"INSERT OR REPLACE INTO {tabla}(id, last_exported_date, status) VALUES (@id, @fecha, @estado);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@fecha", fecha.ToString("s")); // ISO 8601 sin zona horaria
            cmd.Parameters.AddWithValue("@estado", estadoSqlite);
            cmd.ExecuteNonQuery();

            contador++;
        }

        Console.WriteLine($"➡️  Migrados {contador} registros a {tabla}");
    }

    private string TraducirEstado(string estadoLiteDb)
    {
        if (estadoLiteDb == "subido") return "done";
        if (estadoLiteDb == "sin_auditoria") return "empty";
        if (estadoLiteDb == "error_subida") return "error";
        if (estadoLiteDb == "error_subida_reintento") return "retry";
        if (estadoLiteDb == "eliminado") return "deleted";
        return "unknown";
    }
}
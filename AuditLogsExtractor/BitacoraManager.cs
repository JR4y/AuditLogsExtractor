using System;
using LiteDB;

public class BitacoraManager : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly object _lock = new object();

    public BitacoraManager(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
        Console.WriteLine("📒 Bitácora LiteDB cargada.");
    }

    public DateTime? GetUltimaFechaExportada(string entityName, Guid guid)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
            var item = col.FindById(guid);
            return item?.UltimaFechaExportada;
        }
    }

    public void MarkAsExported(string entityName, Guid guid, DateTime fecha, string estado)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
            col.Upsert(new BitacoraItem
            {
                Id = guid,
                UltimaFechaExportada = fecha,
                Estado = estado
            });
        }
    }

    public string GetEstado(string entityName, Guid guid)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
            var item = col.FindById(guid);
            return item?.Estado ?? "desconocido";
        }
    }

    private static string GetCollectionName(string entityName) => $"bitacora_{entityName}";

    public void Dispose() => _db?.Dispose();
}

public class BitacoraItem
{
    [BsonId]
    public Guid Id { get; set; }

    public DateTime UltimaFechaExportada { get; set; }

    public string Estado { get; set; } // Ej: "subido", "omitido", "error_subida"
}
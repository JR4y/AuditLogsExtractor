using LiteDB;
using System;
using System.Collections.Generic;

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

    public IEnumerable<(string Entidad, Guid RecordId, DateTime Fecha)> ObtenerErroresSubida()
    {
        lock (_lock)
        {
            foreach (var colName in _db.GetCollectionNames())
            {
                if (!colName.StartsWith("bitacora_")) continue;

                var entidad = colName.Replace("bitacora_", "");
                var col = _db.GetCollection<BitacoraItem>(colName);

                foreach (var item in col.Find(x => x.Estado == "error_subida"))
                {
                    yield return (entidad, item.Id, item.UltimaFechaExportada);
                }
            }
        }
    }

    public void MarcarReintentoExitoso(string entityName, Guid guid, DateTime fecha)
    {
        MarkAsExported(entityName, guid, fecha, "subido");
    }

    private static string GetCollectionName(string entityName) => $"bitacora_{entityName}";

    public IEnumerable<Guid> ObtenerIdsPorEstado(string entidad, string estado)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>($"bitacora_{entidad}");
            foreach (var item in col.Find(x => x.Estado == estado))
                yield return item.Id;
        }
    }

    public void GuardarResumenEjecucion(ResumenEjecucion resumen)
    {
        try
        {
            lock (_lock)
            {
                var col = _db.GetCollection<ResumenEjecucion>("resumen_ejecucion");
                col.Insert(resumen);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error al guardar resumen de ejecución: {ex.Message}");
        }
    }

    public void Dispose() => _db?.Dispose();
}

    public class BitacoraItem
{
    [BsonId]
    public Guid Id { get; set; }

    public DateTime UltimaFechaExportada { get; set; }

    public string Estado { get; set; } // Ej: "subido", "omitido", "error_subida"
}

    public class ResumenEjecucion
{
    public int Id { get; set; }
    public DateTime FechaEjecucion { get; set; }
    public string Entidad { get; set; }
    public int Total { get; set; }
    public int Omitidos { get; set; }
    public int Exportados { get; set; }
    public int ConErrorSubida { get; set; }
    public TimeSpan Duracion { get; set; }
}
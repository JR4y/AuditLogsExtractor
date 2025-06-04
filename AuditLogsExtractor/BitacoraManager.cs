using System;
using LiteDB;

public class BitacoraManager : IDisposable
{
    private readonly LiteDatabase _db;

    public BitacoraManager(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
        Console.WriteLine("📒 Bitácora LiteDB cargada.");
    }

    public bool Exists(string entityName, Guid guid)
    {
        var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
        return col.Exists(x => x.Id == guid);
    }

    public void MarkAsExported(string entityName, Guid guid)
    {
        var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
        col.Upsert(new BitacoraItem { Id = guid });
    }

    private static string GetCollectionName(string entityName) => $"bitacora_{entityName}";

    public void Dispose() => _db?.Dispose();
}

public class BitacoraItem
{
    [BsonId]
    public Guid Id { get; set; }
}
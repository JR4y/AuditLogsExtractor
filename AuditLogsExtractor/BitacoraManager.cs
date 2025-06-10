using LiteDB;
using System;
using System.Collections.Generic;
using System.Net;
using System.IO;

public class BitacoraManager : IDisposable
{
    #region Constructor and Fields
    private readonly LiteDatabase _db;
    private readonly object _lock = new object();

    public BitacoraManager(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
    }
    #endregion

    #region Record State Management
    public DateTime? GetLastExportedDate(string entityName, Guid guid)
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

    public string GetExportStatus(string entityName, Guid guid)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>(GetCollectionName(entityName));
            var item = col.FindById(guid);
            return item?.Estado ?? "desconocido";
        }
    }

    public IEnumerable<(string Entidad, Guid RecordId, DateTime Fecha)> GetUploadErrors()
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

    public void MarkRetryAsSuccessful(string entityName, Guid guid, DateTime fecha)
    {
        MarkAsExported(entityName, guid, fecha, "subido");
    }

    private static string GetCollectionName(string entityName)
    {
        return $"bitacora_{entityName}";
    }

    public IEnumerable<Guid> GetRecordIdsByStatus(string entityName, string estado)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<BitacoraItem>($"bitacora_{entityName}");
            foreach (var item in col.Find(x => x.Estado == estado))
                yield return item.Id;
        }
    }
    #endregion

    #region Execution Summary
    public void SaveExecutionSummary(ResumenEjecucion resumen)
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

    #endregion

    #region Bitacora File Operations
    public static BitacoraManager DownloadOrCreateBitacora(SharePointUploader uploader, out string backupName, string folder = "BITACORA")
    {
        string fileName = "bitacora.db";
        string relativePath = $"{folder}/{fileName}";
        bool downloaded = false;
        backupName = null;

        try
        {
            uploader.DownloadFile(relativePath, fileName);
            downloaded = true;
        }
        catch (WebException ex)
        {
            if (ex.Response is HttpWebResponse resp && resp.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.Warning("⚠️ Bitácora no encontrada en SharePoint.");
            }
            else
            {
                Logger.Error($"❌ Error descargando bitácora: {ex.Message}");
                throw;
            }
        }

        if (!downloaded && !File.Exists(fileName))
        {
            Logger.Warning("⚠️ Bitácora no encontrada localmente. Se creará nueva.");
            new BitacoraManager(fileName).Dispose();
        }

        if (downloaded)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                backupName = $"bitacora_{timestamp}.db";
                File.Copy(fileName, backupName, true);
            }
            catch (Exception)
            {
                // Intencionalmente ignorado
            }
        }

        return new BitacoraManager(fileName);
    }

    public static void UploadBitacoraAndBackup(SharePointUploader uploader, string backupName, string folder = "BITACORA")
    {
        string fileName = "bitacora.db";
        string relativePath = $"{folder}/{fileName}";
        string backupRelativePath = !string.IsNullOrEmpty(backupName)
            ? $"{folder}/{backupName}"
            : null;

        bool mainUploaded = false;
        bool backupUploaded = false;

        try
        {
            uploader.UploadFile(fileName, relativePath);
            mainUploaded = true;

            if (!string.IsNullOrEmpty(backupRelativePath) && File.Exists(backupName))
            {
                uploader.UploadFile(backupName, backupRelativePath);
                backupUploaded = true;
                Logger.Info("📤 Bitácora y respaldo subidos a SharePoint.", ConsoleColor.Magenta);
            }
            else
            {
                Logger.Warning("⚠️ No se subió backup porque no fue generado o no existe.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Error subiendo bitácora o respaldo: {ex.Message}");
        }

        if (mainUploaded && backupUploaded)
        {
            try
            {
                File.Delete(backupName);
            }
            catch (Exception ex)
            {
                Logger.Warning($"⚠️ No se pudo eliminar backup local: {ex.Message}");
            }
        }
    }

    public void Close()
    {
        try
        {
            _db?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warning($"⚠️ Error al cerrar la bitácora: {ex.Message}");
        }
    }
    #endregion

    #region Folder Verification
    public HashSet<string> GetVerifiedFolders()
    {
        lock (_lock)
        {
            var col = _db.GetCollection<CarpetaVerificada>("carpetas_verificadas");
            var hash = new HashSet<string>();

            foreach (var item in col.FindAll())
            {
                hash.Add($"{item.Entidad}|{item.Prefijo}");
            }

            return hash;
        }
    }

    public void SaveVerifiedFoldersFrom(HashSet<string> list)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<CarpetaVerificada>("carpetas_verificadas");

            foreach (var id in list)
            {
                var parts = id.Split('|');
                if (parts.Length != 2) continue;

                string entity = parts[0];
                string prefix = parts[1];

                if (!col.Exists(x => x.Id == id))
                {
                    col.Insert(new CarpetaVerificada
                    {
                        Id = id,
                        Entidad = entity,
                        Prefijo = prefix
                    });
                }
            }
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
    #endregion
}

#region Data Models
public class BitacoraItem
{
    [BsonId]
    public Guid Id { get; set; }

    public DateTime UltimaFechaExportada { get; set; }

    public string Estado { get; set; }
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
    public string Duracion { get; set; }
}

public class CarpetaVerificada
{
    public string Id { get; set; }
    public string Entidad { get; set; }
    public string Prefijo { get; set; }
}
#endregion
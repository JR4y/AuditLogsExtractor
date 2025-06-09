using LiteDB;
using System;
using System.Collections.Generic;
using System.Net;
using System.IO;

public class BitacoraManager : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly object _lock = new object();
    public BitacoraManager(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
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

    public static BitacoraManager DescargarOBitacora(SharePointUploader uploader, out string nombreBackup, string carpetaBitacora = "BITACORA")
    {
        string nombreArchivo = "bitacora.db";
        string rutaRelativa = $"{carpetaBitacora}/{nombreArchivo}";
        bool descargado = false;
        nombreBackup = null; // ✅ Asegura siempre inicialización

        // 1. Intentar descargar la bitácora desde SharePoint
        try
        {
            uploader.DownloadFile(rutaRelativa, nombreArchivo);
            descargado = true;
            //Logger.Info("📥 Bitácora descargada desde SharePoint.");
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

        // 2. Si no se descargó y no existe localmente → crearla
        if (!descargado && !File.Exists(nombreArchivo))
        {
            Logger.Warning("⚠️ Bitácora no encontrada localmente. Se creará nueva.");
            new BitacoraManager(nombreArchivo).Dispose(); // crea vacía y libera
        }

        // 3. Si se descargó exitosamente → hacer copia local como respaldo
        if (descargado)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                nombreBackup = $"bitacora_{timestamp}.db";
                File.Copy(nombreArchivo, nombreBackup, true);
                //Logger.Info($"🗂️ Backup local creado: {nombreBackup}");
            }
            catch (Exception ex)
            {
                //Logger.Warning($"⚠️ No se pudo generar backup local de la bitácora: {ex.Message}");
            }
        }

        // 4. Crear y retornar instancia principal
        return new BitacoraManager(nombreArchivo);
    }

    public static void SubirBitacoraYBackup(SharePointUploader uploader, string nombreBackup, string carpetaBitacora = "BITACORA")
    {
        string nombreArchivo = "bitacora.db";
        string rutaRelativa = $"{carpetaBitacora}/{nombreArchivo}";
        string rutaBackupRelativa = !string.IsNullOrEmpty(nombreBackup)
            ? $"{carpetaBitacora}/{nombreBackup}"
            : null;

        bool subidaPrincipal = false;
        bool subidaBackup = false;

        try
        {
            // Subir archivo principal (bitácora actual)
            uploader.UploadFile(nombreArchivo, rutaRelativa);
            subidaPrincipal = true;

            // Subir backup solo si se generó uno válido
            if (!string.IsNullOrEmpty(rutaBackupRelativa) && File.Exists(nombreBackup))
            {
                uploader.UploadFile(nombreBackup, rutaBackupRelativa);
                subidaBackup = true;
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

        if (subidaPrincipal && subidaBackup)
        {
            try
            {
                File.Delete(nombreBackup);
                //Logger.Info($"🧹 Backup local eliminado: {nombreBackup}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"⚠️ No se pudo eliminar backup local: {ex.Message}");
            }
        }
    }

    public void Cerrar()
    {
        try
        {
            _db?.Dispose();
            //Logger.Trace("🗃️ Bitácora cerrada correctamente.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"⚠️ Error al cerrar la bitácora: {ex.Message}");
        }
    }
    public HashSet<string> ObtenerCarpetasVerificadas()
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
    public void GuardarCarpetasVerificadasDesde(HashSet<string> lista)
    {
        lock (_lock)
        {
            var col = _db.GetCollection<CarpetaVerificada>("carpetas_verificadas");

            foreach (var id in lista)
            {
                var partes = id.Split('|');
                if (partes.Length != 2) continue;

                string entidad = partes[0];
                string prefijo = partes[1];

                // Solo insertar si no existe en la base de datos
                if (!col.Exists(x => x.Id == id))
                {
                    col.Insert(new CarpetaVerificada
                    {
                        Id = id,
                        Entidad = entidad,
                        Prefijo = prefijo
                    });
                }
            }
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
    public string Duracion { get; set; }
}

public class CarpetaVerificada
{
    public string Id { get; set; } // entidad|prefijo
    public string Entidad { get; set; }
    public string Prefijo { get; set; }
}
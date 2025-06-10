using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class AuditOrchestrator
{
    #region Campos y Constructor
    private readonly DynamicsReader _readerProd;
    private readonly AuditProcessor _processor;
    private readonly CsvExporter _exporter;
    private readonly SharePointUploader _uploader;
    private readonly BitacoraManager _bitacora;
    private readonly string _backupName;
    private readonly List<(string logicalName, int otc)> _entidades;
    private readonly DateTime _fechaCorte;
    private readonly CancellationToken _token;
    private static int _bitacoraSubidaFlag = 0;
    public AuditOrchestrator(
        DynamicsReader readerProd,
        AuditProcessor processor,
        CsvExporter exporter,
        SharePointUploader uploader,
        BitacoraManager bitacora,
        string backupName,
        List<(string logicalName, int otc)> entidades,
        DateTime fechaCorte,
        CancellationToken token)
    {
        _readerProd = readerProd;
        _processor = processor;
        _exporter = exporter;
        _uploader = uploader;
        _bitacora = bitacora;
        _backupName = backupName;
        _entidades = entidades;
        _fechaCorte = fechaCorte;
        _token = token;
    }
    #endregion

    #region Proceso principal
    public void Ejecutar()
    {
        EjecutarReintentosFallidos();

        foreach (var (entidad, otc) in _entidades)
        {
            Logger.Info($"======== 📁 Procesando entidad: {entidad} ========", ConsoleColor.Blue);

            List<Guid> recordIds = _readerProd.GetRecordIds(entidad, _fechaCorte);
            var total = recordIds.Count;
            var totalActual = 0;

            var guidsProcesados = _bitacora.GetRecordIdsByStatus(entidad, "subido")
                .Concat(_bitacora.GetRecordIdsByStatus(entidad, "sin_auditoria"))
                .ToHashSet();

            int procesados = 0, errores = 0, sinAuditoria = 0, prevProcesados = 0;
            var inicioEntidad = DateTime.Now;
            prevProcesados = recordIds.Count(id => guidsProcesados.Contains(id));
            recordIds = recordIds.Where(id => !guidsProcesados.Contains(id)).ToList();

            try
            {
                Parallel.ForEach(recordIds, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 5,
                    CancellationToken = _token
                }, recordId =>
                {
                    ProcesarRegistro(entidad, otc, recordId, _fechaCorte, ref procesados, ref errores, ref sinAuditoria, _token);

                    totalActual = procesados + sinAuditoria + errores + prevProcesados;
                    if (totalActual % 10 == 0)
                    {
                        Logger.Progreso(entidad, total, totalActual, procesados, prevProcesados, sinAuditoria, errores);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("⏸️ Proceso pausado. Puede reanudarse sin pérdida de información.");
                break; // Sal del foreach de entidades directamente;
            }
            finally
            {
                var duracion = DateTime.Now - inicioEntidad;
                totalActual = procesados + sinAuditoria + errores + prevProcesados;
                GuardarResumenEntidad(entidad, totalActual, sinAuditoria, procesados, errores, prevProcesados, duracion);
            }
        }
        EjecutarReintentosFallidos();
        CerrarYSubirBitacora();
    }
    #endregion

    #region Procesamiento individual
    private void ProcesarRegistro(
        string entidad,
        int otc,
        Guid recordId,
        DateTime fechaCorte,
        ref int procesados,
        ref int errores,
        ref int sinAuditoria,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        try
        {
            DateTime? ultimaFecha = _bitacora.GetLastExportedDate(entidad, recordId);
            List<Entity> registros = _processor.GetAuditRecords(entidad, otc, recordId, fechaCorte);

            if (registros == null || registros.Count == 0)
            {
                Interlocked.Increment(ref sinAuditoria);
                _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "sin_auditoria");
                return;
            }

            var nuevos = registros
                .Where(r => !ultimaFecha.HasValue || r.GetAttributeValue<DateTime>("createdon") > ultimaFecha)
                .ToList();

            if (nuevos.Count == 0)
            {
                Interlocked.Increment(ref sinAuditoria);
                _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "sin_auditoria");
                return;
            }

            string archivo = _exporter.ExportGroupAsCsv(entidad, recordId, nuevos);
            string prefijo = recordId.ToString().Substring(0, 2);
            string nombreArchivo = Path.GetFileName(archivo);
            string rutaRelativa = $"{entidad}/{prefijo}/{nombreArchivo}";

            try
            {
                _uploader.UploadFile(archivo, rutaRelativa, entidad);
                _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "subido");

                if (File.Exists(archivo))
                    File.Delete(archivo);

                Interlocked.Increment(ref procesados);
            }
            catch (Exception)
            {
                Logger.Error($"❌ Error al subir/eliminar archivo {archivo}");
                _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "error_subida");
                Interlocked.Increment(ref errores);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref errores);
            Logger.Error($"❌ Error al procesar {entidad} - {recordId}: {ex.Message}");
        }
    }
    #endregion

    #region Guardado resumen y bitácora
    private void GuardarResumenEntidad(string entidad, int totalActual, int sinAuditoria, int procesados, int errores, int prevProcesados, TimeSpan duracion)
    {
        Logger.Ok($"Resumen {entidad} - Exportados: {procesados}, Sin audit: {sinAuditoria}, Previos: {prevProcesados}, Errores: {errores}, Tiempo: {duracion:mm\\:ss}");

        try
        {
            _bitacora.SaveExecutionSummary(new ResumenEjecucion
            {
                FechaEjecucion = DateTime.Now,
                Entidad = entidad,
                Total = totalActual,
                Omitidos = sinAuditoria,
                Exportados = procesados,
                ConErrorSubida = errores,
                Duracion = duracion.ToString(@"hh\:mm\:ss")
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Error al guardar resumen de entidad '{entidad}': {ex.Message}");
        }
    }

    private void CerrarYSubirBitacora()
    {
        if (Interlocked.Exchange(ref _bitacoraSubidaFlag, 1) == 1) return;

        try
        {
            _bitacora.SaveVerifiedFoldersFrom(_uploader.GetCarpetasVerificadas());
            _bitacora.Close();
            BitacoraManager.UploadBitacoraAndBackup(_uploader, _backupName);
            Logger.Info("📤 Bitácora y respaldo subidos a SharePoint.", ConsoleColor.Magenta);
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Error al cerrar/subir bitácora: {ex.Message}");
        }
    }
    #endregion

    #region Reintentos
    private void EjecutarReintentosFallidos()
    {
        Logger.Info("♻️ Iniciando reintento de subidas fallidas...", ConsoleColor.DarkYellow);

        foreach (var (entidad, recordId, fecha) in _bitacora.GetUploadErrors())
        {
            string archivo = $"output\\{recordId}.csv";
            if (!File.Exists(archivo))
            {
                Logger.Warning($"⚠️ Archivo pendiente no encontrado: {archivo}");
                continue;
            }

            try
            {
                string prefijo = recordId.ToString().Substring(0, 2);
                string rutaRelativa = $"{entidad}/{prefijo}/{Path.GetFileName(archivo)}";

                _uploader.UploadFile(archivo, rutaRelativa, entidad);
                _bitacora.MarkRetryAsSuccessful(entidad, recordId, fecha);
                File.Delete(archivo);

                Logger.Info($"🔁 Reintentando para entidad '{entidad}', ID = {recordId}", ConsoleColor.DarkGray);
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Reintento fallido para {archivo}: {ex.Message}");
                _bitacora.MarkAsExported(entidad, recordId, fecha, "error_subida_reintento");
            }
        }

        Logger.Info("🛑 Finalizado el proceso de reintentos.", ConsoleColor.DarkYellow);
    }
    #endregion
}

using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AuditLogsExtractor
{
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
        private readonly Action<EstadoEntidadActual> _estadoCallback;
        public AuditOrchestrator(
            DynamicsReader readerProd,
            AuditProcessor processor,
            CsvExporter exporter,
            SharePointUploader uploader,
            BitacoraManager bitacora,
            string backupName,
            List<(string logicalName, int otc)> entidades,
            DateTime fechaCorte,
            CancellationToken token,
            Action<EstadoEntidadActual> estadoCallback)
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
            _estadoCallback = estadoCallback;
        }
        #endregion

        #region Proceso principal Uno a Uno
        public void Ejecutar()
        {
            EjecutarReintentosFallidos();

            foreach (var (entidad, otc) in _entidades)
            {
                List<string> recordIds = _readerProd.GetRecordIds(entidad, _fechaCorte);
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
                            var estado = new EstadoEntidadActual
                            {
                                Entidad = entidad,
                                Total = total,
                                Actual = totalActual,
                                Exportados = procesados,
                                SinAuditoria = sinAuditoria,
                                Previos = prevProcesados,
                                Errores = errores,
                                Duracion = DateTime.Now - inicioEntidad
                            };

                            _estadoCallback?.Invoke(estado);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("Proceso pausado. Puede reanudarse sin pérdida de información.", "WARN");
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
            string recordId,
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
                    Logger.Log($"Error al subir/eliminar archivo {archivo}", "ERROR");
                    _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "error_subida");
                    Interlocked.Increment(ref errores);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errores);
                Logger.Log($"Error al procesar {entidad} - {recordId}: {ex.Message}");
            }
        }
        #endregion

        #region Guardado resumen y bitácora
        private void GuardarResumenEntidad(string entidad, int totalActual, int sinAuditoria, int procesados, int errores, int prevProcesados, TimeSpan duracion)
        {
            //Logger.Log($"Resumen {entidad} - Exportados: {procesados}, Sin audit: {sinAuditoria}, Previos: {prevProcesados}, Errores: {errores}, Tiempo: {duracion:mm\\:ss}","OK");
            Logger.Log($"Resumen {entidad} - Total procesados: {prevProcesados}","OK");

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
                Logger.Log($"Error al guardar resumen de entidad '{entidad}': {ex.Message}");
            }
        }

        private void CerrarYSubirBitacora()
        {
            if (Interlocked.Exchange(ref _bitacoraSubidaFlag, 1) == 1) return;

            try
            {
                _bitacora.SaveVerifiedFoldersFrom(_uploader.GetVerifiedFolders());
                _bitacora.Close();
                BitacoraManager.UploadBitacoraAndBackup(_uploader, _backupName);
                Logger.Log("📤 Bitácora y respaldo subidos a SharePoint.", "", ConsoleColor.Magenta);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error al cerrar/subir bitácora: {ex.Message}");
            }
        }
        #endregion

        #region Reintentos
        private void EjecutarReintentosFallidos()
        {
            Logger.Log("Iniciando reintento de subidas fallidas...", "", ConsoleColor.DarkYellow);

            foreach (var (entidad, recordId, fecha) in _bitacora.GetUploadErrors())
            {
                string archivo = $"output\\{recordId}.csv";
                if (!File.Exists(archivo))
                {
                    Logger.Log($"⚠️ Archivo pendiente no encontrado: {archivo}", "WARN");
                    continue;
                }

                try
                {
                    string prefijo = recordId.ToString().Substring(0, 2);
                    string rutaRelativa = $"{entidad}/{prefijo}/{Path.GetFileName(archivo)}";

                    _uploader.UploadFile(archivo, rutaRelativa, entidad);
                    _bitacora.MarkRetryAsSuccessful(entidad, recordId, fecha);
                    File.Delete(archivo);

                    Logger.Log($"Reintentando para entidad '{entidad}', ID = {recordId}", "", ConsoleColor.DarkGray);
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Reintento fallido para {archivo}: {ex.Message}", "ERROR");
                    _bitacora.MarkAsExported(entidad, recordId, fecha, "error_subida_reintento");
                }
            }

            Logger.Log("Finalizado el proceso de reintentos.", "INFO", ConsoleColor.DarkYellow);
        }
        #endregion

        #region Proceso principal ZIP
        public void EjecutarZip()
        {
            var carpetasYaVerificadas = _bitacora.GetVerifiedFolders(); // ← clave para evitar reprocesar
            foreach (var (entidad, otc) in _entidades)
            {
                Logger.Log($"======== 📁 [ZIP] Procesando entidad: {entidad} ========", "", ConsoleColor.Cyan);

                List<string> recordIds = _readerProd.GetRecordIds(entidad, _fechaCorte);
                var guidsProcesados = _bitacora.GetRecordIdsByStatus(entidad, "subido")
                    .Concat(_bitacora.GetRecordIdsByStatus(entidad, "sin_auditoria"))
                    .ToHashSet();

                var pendientes = recordIds.Where(id => !guidsProcesados.Contains(id)).ToList();

                // Agrupar por prefijo (00, 01, ..., FF)
                var gruposPorPrefijo = pendientes
                    .GroupBy(id => id.Substring(0, 2))
                    .OrderBy(g => g.Key)
                    .ToList();

                int total = recordIds.Count;
                int procesados = 0, errores = 0, sinAuditoria = 0;
                int prevProcesados = recordIds.Count - pendientes.Count;
                var inicioEntidad = DateTime.Now;

                foreach (var grupo in gruposPorPrefijo)
                {
                    if (_token.IsCancellationRequested)
                    {
                        Logger.Log("⏸️ Pausa detectada. Finalizando ejecución al terminar prefijo actual.", "WARN");
                        break;
                    }

                    string prefijo = grupo.Key;
                    total = grupo.Count();
                    procesados = 0;
                    errores = 0; 
                    sinAuditoria = 0;
                    prevProcesados = recordIds.Count - pendientes.Count;
                    inicioEntidad = DateTime.Now;

                    // Saltar si ya está marcado como verificado
                    string keyVerificacion = $"{entidad}|{prefijo}";
                    if (carpetasYaVerificadas.Contains(keyVerificacion))
                    {
                        Logger.Log($"Prefijo '{prefijo}' ya procesado previamente, se omite.","OK");
                        continue;
                    }

                        //Logger.Log($"→ Prefijo '{prefijo}' ({grupo.Count()} registros)");

                        foreach (var recordId in grupo)
                        {
                            ProcesarRegistroSinSubida(entidad, otc, recordId, _fechaCorte, ref procesados, ref errores, ref sinAuditoria, _token);

                            var estado = new EstadoEntidadActual
                            {
                                Entidad = entidad,
                                Total = grupo.Count(),
                                Actual = procesados + sinAuditoria + errores,
                                Exportados = procesados,
                                SinAuditoria = sinAuditoria,
                                Previos = prevProcesados,
                                Errores = errores,
                                Duracion = DateTime.Now - inicioEntidad,
                                Prefijo = prefijo,
                                ZIP = true
                            };

                            _estadoCallback?.Invoke(estado);
                        }

                    ComprimirYSubirZip(entidad, prefijo);

                    // Marcar el prefijo como verificado
                    _bitacora.MarcarCarpetaVerificada(entidad, prefijo);
                }

                var duracion = DateTime.Now - inicioEntidad;
                int totalActual = procesados + sinAuditoria + errores + prevProcesados;
                GuardarResumenEntidad(entidad, totalActual, sinAuditoria, procesados, errores, prevProcesados, duracion);
            }

            CerrarYSubirBitacora();
        }
        private void ProcesarRegistroSinSubida(string entidad, int otc, string recordId, DateTime fechaCorte, ref int procesados, ref int errores, ref int sinAuditoria, CancellationToken token)
        {
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
                _bitacora.MarkAsExported(entidad, recordId, fechaCorte, "exportado_no_subido");
                Interlocked.Increment(ref procesados);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errores);
                Logger.Log($"Error al procesar [ZIP] {entidad} - {recordId}: {ex.Message}", "ERROR");
            }
        }

        private void ComprimirYSubirZip(string entidad, string prefijo)
        {
            try
            {
                string carpetaOrigen = Path.Combine("output", entidad, prefijo);
                if (!Directory.Exists(carpetaOrigen))
                {
                    Logger.Log($"Carpeta de prefijo no encontrada: {carpetaOrigen}", "WARN");
                    return;
                }

                var archivosCsv = Directory.GetFiles(carpetaOrigen, "*.csv").ToList();
                if (archivosCsv.Count == 0)
                {
                    Logger.Log($"No hay archivos CSV para comprimir en '{carpetaOrigen}'", "WARN");
                    return;
                }

                int loteSize = 100;
                int totalZips = (int)Math.Ceiling(archivosCsv.Count / (double)loteSize);
                bool fallo = false;

                for (int i = 0; i < totalZips; i++)
                {
                    string tempFolder = Path.Combine("temp_zip", entidad, prefijo);
                    Directory.CreateDirectory(tempFolder);

                    var archivosDelLote = archivosCsv.Skip(i * loteSize).Take(loteSize).ToList();
                    List<string> guidsDelLote = new List<string>();

                    foreach (var archivo in archivosDelLote)
                    {
                        string destino = Path.Combine(tempFolder, Path.GetFileName(archivo));
                        File.Copy(archivo, destino, true);

                        // Extraer el recordId desde el nombre del archivo
                        string nombre = Path.GetFileNameWithoutExtension(archivo);
                        guidsDelLote.Add(nombre);
                    }

                    string sufijoZip = totalZips == 1 ? "" : $"_part{i + 1}";
                    string nombreZip = $"{prefijo}{sufijoZip}.zip";
                    string rutaZip = Path.Combine("output", entidad, nombreZip);

                    if (File.Exists(rutaZip))
                        File.Delete(rutaZip);

                    System.IO.Compression.ZipFile.CreateFromDirectory(tempFolder, rutaZip);
                    string rutaRelativa = $"{entidad}/{prefijo}/{nombreZip}";

                    try
                    {
                        _uploader.UploadZipFile(rutaZip, rutaRelativa);
                        File.Delete(rutaZip);

                        foreach (var id in guidsDelLote)
                        {
                            _bitacora.MarkAsExported(entidad, id, _fechaCorte, "subido");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error subiendo ZIP {nombreZip}: {ex.Message}", "ERROR");
                        fallo = true;
                        break;
                    }

                    Directory.Delete(tempFolder, true);
                }

                if (!fallo)
                {
                    Directory.Delete(carpetaOrigen, true);
                    Logger.Log($"ZIPs subidos y bitácora actualizada para prefijo '{prefijo}'", "OK");
                }
                else
                {
                    Logger.Log($"Subida incompleta, se conservan archivos y bitácora parcial para prefijo '{prefijo}'", "WARN");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error en proceso de ZIP por partes para entidad '{entidad}', prefijo '{prefijo}': {ex.Message}", "ERROR");
            }
        }
        #endregion

        #region Modelo de Estado Actual
        public class EstadoEntidadActual
        {
            public string Entidad { get; set; }
            public string Prefijo { get; set; }
            public int Total { get; set; }
            public int Actual { get; set; }
            public int Exportados { get; set; }
            public int SinAuditoria { get; set; }
            public int Previos { get; set; }
            public int Errores { get; set; }
            public TimeSpan Duracion { get; set; }
            public bool ZIP { get; set; } = false; // Indica si es un proceso ZIP
        }
        #endregion

    }
}

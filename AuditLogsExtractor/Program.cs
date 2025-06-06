using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        Logger.Info("Iniciando extractor de auditoría...");

        try
        {
            string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
            string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

            var readerDev = new DynamicsReader(connDev);
            var config = readerDev.ObtenerParametrosDeConfiguracion();
            var readerProd = new DynamicsReader(connProd);

            int mesesConservar = int.Parse(config["meses_conservar"]);
            DateTime fechaCorte = DateTime.UtcNow.AddMonths(-mesesConservar);
            Logger.Info($"Extrayendo auditoría anterior a: {fechaCorte:yyyy-MM-dd}");

            int totalEntidades = int.Parse(config["total_entidades"]);
            var entidades = new List<(string logicalName, int otc)>();

            for (int i = 1; i <= totalEntidades; i++)
            {
                string logicalName = config[$"entidad_{i}_logicalname"];
                int otc = int.Parse(config[$"entidad_{i}_otc"]);
                entidades.Add((logicalName, otc));
            }

            var bitacora = new BitacoraManager("bitacora.db");
            var processor = new AuditProcessor(readerProd.ObtenerServicio());
            var exporter = new CsvExporter(readerProd.ObtenerServicio());
            var uploader = new SharePointUploader(
                config["sp_site"],
                config["sp_upload_folder"],
                config["sp_user"],
                config["sp_password"]);

            EjecutarReintentosFallidos(bitacora, uploader);

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            FileSystemWatcher watcher = new FileSystemWatcher(Directory.GetCurrentDirectory(), "pause.signal")
            {
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) =>
            {
                Logger.Warning("⚠️ Pausa detectada. Finalizando ejecución de forma segura...");
                cts.Cancel();
            };

            foreach (var (entidad, otc) in entidades)
            {
                Logger.Info($"======== 📁 Procesando entidad: {entidad} ========");

                List<Guid> recordIds = readerProd.ObtenerRecordIds(entidad, fechaCorte);

                var guidsProcesados = bitacora.ObtenerIdsPorEstado(entidad, "subido").Concat(bitacora.ObtenerIdsPorEstado(entidad, "sin_auditoria")).ToHashSet();

                //var total = recordIds.Count(id => !guidsProcesados.Contains(id));

                int procesados = 0, errores = 0, sinAuditoria = 0, prevProcesados = 0;
                var inicioEntidad = DateTime.Now;
                prevProcesados = recordIds.Count(id => guidsProcesados.Contains(id));
                // Obtiene los registros excluyendo los previamente procesados
                recordIds = recordIds.Where(id => !guidsProcesados.Contains(id)).ToList();
                var total = recordIds.Count;


                try
                {
                    Parallel.ForEach(recordIds, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = token
                    }, recordId =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        try
                        {
                            /*if (guidsProcesados.Contains(recordId))
                            {
                                Interlocked.Increment(ref prevProcesados);
                                return;
                            }*/

                            DateTime? ultimaFecha = bitacora.GetUltimaFechaExportada(entidad, recordId);
                            List<Entity> registros = processor.ObtenerAuditoria(entidad, otc, recordId, fechaCorte);

                            if (registros == null || registros.Count == 0)
                            {
                                Interlocked.Increment(ref sinAuditoria);
                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "sin_auditoria");
                                return;
                            }

                            var nuevos = registros
                                .Where(r => !ultimaFecha.HasValue || r.GetAttributeValue<DateTime>("createdon") > ultimaFecha)
                                .ToList();

                            if (nuevos.Count == 0)
                            {
                                Interlocked.Increment(ref sinAuditoria);
                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "sin_auditoria");
                                return;
                            }

                            string archivo = exporter.ExportarGrupoComoCsv(entidad, recordId, nuevos);
                            string prefijo = recordId.ToString().Substring(0, 2);
                            string nombreArchivo = Path.GetFileName(archivo);
                            string rutaRelativa = $"{entidad}/{prefijo}/{nombreArchivo}";

                            try
                            {
                                uploader.UploadFile(archivo, rutaRelativa);
                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "subido");

                                if (File.Exists(archivo))
                                    File.Delete(archivo);

                                Interlocked.Increment(ref procesados);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"❌ Error al subir/eliminar archivo {archivo}: {ex.Message}");
                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "error_subida");
                                Interlocked.Increment(ref errores);
                            }

                            var totalActual = procesados + sinAuditoria + errores + prevProcesados;
                            if (totalActual % 10 == 0)
                            {
                                double avance = (double)totalActual / total * 100;
                                lock (typeof(Logger))
                                {
                                    Console.ForegroundColor = ConsoleColor.Cyan;
                                    Console.Write($"\r[{DateTime.Now:HH:mm:ss}] {entidad}>> {avance:0.#}% ({totalActual}/{total})-[Act:{procesados} | Prev:{prevProcesados} | S/Audit:{sinAuditoria} | Err:{errores}]");
                                    Console.ResetColor();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errores);
                            Logger.Error($"❌ Error al procesar {entidad} - {recordId}: {ex.Message}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    Logger.Warning("⏸️ Proceso pausado. Puede reanudarse sin pérdida de información.");
                    throw;
                }

                finally
                {
                    var dur = DateTime.Now - inicioEntidad;
                    string resHora = DateTime.Now.ToString("HH:mm:ss");
                    Logger.Ok($"[{resHora}] ✅ Resumen {entidad} → Exportados: {procesados}, Sin auditoría: {sinAuditoria}, Previos: {prevProcesados}, Errores: {errores}, Tiempo: {dur:mm\\:ss}");
                    try
                    {
                        bitacora.GuardarResumenEjecucion(new ResumenEjecucion
                        {
                            FechaEjecucion = DateTime.Now,
                            Entidad = entidad,
                            Total = total,
                            Omitidos = sinAuditoria,
                            Exportados = procesados,
                            ConErrorSubida = errores,
                            Duracion = dur
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"❌ Error al guardar resumen: {ex.Message}");
                    }
                }               
                EjecutarReintentosFallidos(bitacora, uploader);
            }

            Logger.Ok("Extracción de auditoría finalizada con éxito.");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("⏹️ Extracción cancelada manualmente.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString());
        }
    }

    private static void EjecutarReintentosFallidos(BitacoraManager bitacora, SharePointUploader uploader)
    {
        Logger.Info("♻️ Iniciando reintento de subidas fallidas...");

        foreach (var (entidad, recordId, fecha) in bitacora.ObtenerErroresSubida())
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

                uploader.UploadFile(archivo, rutaRelativa);
                bitacora.MarcarReintentoExitoso(entidad, recordId, fecha);
                File.Delete(archivo);

                Logger.Ok($"✅ Reintento exitoso para {archivo}");
                Logger.Trace($"🔁 Reintentando para entidad '{entidad}', ID = {recordId}");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Reintento fallido para {archivo}: {ex.Message}");
            }
        }

        Logger.Info("🛑 Finalizado el proceso de reintentos.");
    }
}

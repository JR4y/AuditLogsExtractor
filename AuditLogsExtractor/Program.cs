using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
            string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

            var readerDev = new DynamicsReader(connDev);
            var config = readerDev.ObtenerParametrosDeConfiguracion();
            var readerProd = new DynamicsReader(connProd);

            int mesesConservar = int.Parse(config["meses_conservar"]);
            DateTime fechaCorte = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-mesesConservar);
            Logger.Info($"Iniciando proceso de extracción  - (Anteriores a {fechaCorte.ToString("MMM yy", new System.Globalization.CultureInfo("es-ES"))})");

            int totalEntidades = int.Parse(config["total_entidades"]);
            var entidades = new List<(string logicalName, int otc)>();

            for (int i = 1; i <= totalEntidades; i++)
            {
                string logicalName = config[$"entidad_{i}_logicalname"];
                int otc = int.Parse(config[$"entidad_{i}_otc"]);
                entidades.Add((logicalName, otc));
            }

            var processor = new AuditProcessor(readerProd.ObtenerServicio());
            var exporter = new CsvExporter(readerProd.ObtenerServicio());
            var uploader = new SharePointUploader(
                config["sp_site"],
                config["sp_upload_folder"],
                config["sp_user"],
                config["sp_password"]);

            string nombreBackup;
            var bitacora = BitacoraManager.DescargarOBitacora(uploader, out nombreBackup);
            Logger.Info("Bitácora local lista. (descargada de SharePoint y respaldo creado)", ConsoleColor.Magenta);

            var carpetasVerificadas = bitacora.ObtenerCarpetasVerificadas();
            uploader.SetCarpetasVerificadas(carpetasVerificadas);

            EjecutarReintentosFallidos(bitacora, uploader);

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            FileSystemWatcher watcher = new FileSystemWatcher(Directory.GetCurrentDirectory(), "pause.signal")
            {
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) =>
            {
                Console.WriteLine(); // fuerza salto desde el progreso
                Logger.Warning("⚠️ Pausa detectada. Finalizando ejecución de forma segura...");
                cts.Cancel();
            };

            foreach (var (entidad, otc) in entidades)
            {
                Logger.Info($"======== 📁 Procesando entidad: {entidad} ========", ConsoleColor.Blue);

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
                                uploader.UploadFile(archivo, rutaRelativa,entidad);
                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "subido");

                                if (File.Exists(archivo))
                                    File.Delete(archivo);

                                Interlocked.Increment(ref procesados);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(); // fuerza salto desde el progreso
                                Logger.Error($"❌ Error al subir/eliminar archivo {archivo}");
                                //Logger.Error($"Excepción: {ex.GetType().Name} - {ex.Message} | StackTrace: {ex.StackTrace}");

                                bitacora.MarkAsExported(entidad, recordId, fechaCorte, "error_subida");
                                Interlocked.Increment(ref errores);
                            }

                            var totalActual = procesados + sinAuditoria + errores + prevProcesados;
                            if (totalActual % 10 == 0)
                            {
                                double avance = (double)totalActual / total * 100;
                                lock (typeof(Logger))
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                                    Console.Write($"\r[{DateTime.Now:HH:mm:ss}] {entidad} >> {avance:0.#}% ({totalActual}/{total}) - [ Act:{procesados} | Prev:{prevProcesados} | S/Audit:{sinAuditoria} | Err:{errores} ]");
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
                    Logger.Ok($"Resumen {entidad} - Exportados: {procesados}, Sin audit: {sinAuditoria}, Previos: {prevProcesados}, Errores: {errores}, Tiempo: {dur:mm\\:ss}");
                    bitacora.GuardarCarpetasVerificadasDesde(uploader.GetCarpetasVerificadas());
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
                            Duracion = dur.ToString(@"hh\:mm\:ss")
                        });

                        bitacora.Cerrar(); // ← libera el archivo
                        BitacoraManager.SubirBitacoraYBackup(uploader, nombreBackup);
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
            //Logger.Warning("⏹️ Extracción cancelada manualmente.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString());
        }
    }

    private static void EjecutarReintentosFallidos(BitacoraManager bitacora, SharePointUploader uploader)
    {
        Logger.Info("♻️ Iniciando reintento de subidas fallidas...", ConsoleColor.DarkYellow);

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

                uploader.UploadFile(archivo, rutaRelativa,entidad);
                bitacora.MarcarReintentoExitoso(entidad, recordId, fecha);
                File.Delete(archivo);

                //Logger.Ok($"✅ Reintento exitoso para {archivo}");
                Logger.Trace($"🔁 Reintentando para entidad '{entidad}', ID = {recordId}");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Reintento fallido para {archivo}: {ex.Message}");
                bitacora.MarkAsExported(entidad, recordId, fecha, "error_subida_reintento");
            }
        }

        Logger.Info("🛑 Finalizado el proceso de reintentos.", ConsoleColor.DarkYellow);
    }

}

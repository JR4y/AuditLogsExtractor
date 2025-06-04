using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
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

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            // 🟡 Lanzar monitor de archivo de pausa
            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (File.Exists("pause.signal"))
                    {
                        Logger.Warning("⚠️ Pausa detectada. Finalizando ejecución de forma segura...");
                        cts.Cancel();
                        break;
                    }
                    Thread.Sleep(1000);
                }
            });

            foreach (var (entidad, otc) in entidades)
            {
                Logger.Info($"======== 📁 Procesando entidad: {entidad} ========");

                List<Guid> recordIds = readerProd.ObtenerRecordIds(entidad, fechaCorte);
                var total = recordIds.Count;
                int procesados = 0, omitidos = 0, errores = 0;
                var inicioEntidad = DateTime.Now;

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
                            Interlocked.Increment(ref omitidos);
                            return;
                        }

                        var nuevos = new List<Entity>();
                        foreach (var r in registros)
                        {
                            var fechaRegistro = r.GetAttributeValue<DateTime>("createdon");
                            if (ultimaFecha == null || fechaRegistro > ultimaFecha)
                                nuevos.Add(r);
                        }

                        if (nuevos.Count == 0)
                        {
                            Interlocked.Increment(ref omitidos);
                            return;
                        }

                        string archivo = exporter.ExportarGrupoComoCsv(entidad, recordId, nuevos);
                        string prefijo = recordId.ToString().Substring(0, 2);
                        string nombreArchivo = Path.GetFileName(archivo);
                        string rutaRelativa = $"{entidad}/{prefijo}/{nombreArchivo}";

                        try
                        {
                            uploader.UploadFile(archivo, rutaRelativa);
                            bitacora.MarkAsExported(entidad, recordId, fechaCorte);

                            // ✅ Eliminación segura del archivo local
                            if (File.Exists(archivo))
                            {
                                File.Delete(archivo);
                                //Logger.Trace($"🧹 Eliminado: {Path.GetFileName(archivo)}");
                            }

                            Interlocked.Increment(ref procesados);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"❌ Error al subir/eliminar archivo {archivo}: {ex.Message}");
                            Interlocked.Increment(ref errores);
                        }

                        var totalActual = procesados + omitidos + errores;
                        if (totalActual % 10 == 0)
                        {
                            double avance = (double)totalActual / total * 100;
                            lock (typeof(Logger))
                            {
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.Write($"\r[Progreso] {DateTime.Now:HH:mm:ss} > {entidad}: {avance:0.#}% ({totalActual}/{total})   ");
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

                if (token.IsCancellationRequested)
                {
                    Logger.Warning("⏸️ Proceso pausado. Puede reanudarse sin pérdida de información.");
                    return;
                }

                var duracion = DateTime.Now - inicioEntidad;
                string resumenHora = DateTime.Now.ToString("HH:mm:ss");
                Logger.Ok($"[{resumenHora}] ✅ Resumen {entidad} → Exportados: {procesados}, Omitidos: {omitidos}, Errores: {errores}, Tiempo: {duracion:mm\\:ss}");
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
}
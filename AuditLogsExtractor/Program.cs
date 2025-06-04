using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using Microsoft.Xrm.Sdk;

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

            foreach (var (entidad, otc) in entidades)
            {
                Logger.Info($"Procesando entidad: {entidad}");

                List<Guid> recordIds = readerProd.ObtenerRecordIds(entidad, fechaCorte);

                foreach (var recordId in recordIds)
                {
                    DateTime? ultimaFecha = bitacora.GetUltimaFechaExportada(entidad, recordId);

                    List<Entity> registros = processor.ObtenerAuditoria(entidad, otc, recordId, fechaCorte);

                    if (registros == null || registros.Count == 0)
                        continue;

                    var nuevos = new List<Entity>();
                    foreach (var r in registros)
                    {
                        var fechaRegistro = r.GetAttributeValue<DateTime>("createdon");
                        if (ultimaFecha == null || fechaRegistro > ultimaFecha)
                        {
                            nuevos.Add(r);
                        }
                    }

                    if (nuevos.Count == 0)
                        continue;

                    string archivo = exporter.ExportarGrupoComoCsv(entidad, recordId, nuevos);

                    string prefijo = recordId.ToString().Substring(0, 2);
                    string nombreArchivo = Path.GetFileName(archivo);
                    string rutaRelativa = $"{entidad}/{prefijo}/{nombreArchivo}";
                    uploader.UploadFile(archivo, rutaRelativa);

                    bitacora.MarkAsExported(entidad, recordId, fechaCorte);

                    Logger.Ok($"Exportado: {archivo}");

                    if (System.IO.File.Exists("pause.signal"))
                    {
                        Logger.Warning("⚠️ Pausa solicitada. Proceso detenido.");
                        return;
                    }
                }
            }

            Logger.Ok("Extracción de auditoría finalizada con éxito.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex.ToString());
        }
    }
}
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;

namespace AuditLogsExtractor
{
    public class AuditRunner
    {
        public void Ejecutar(CancellationToken token, Action<string> logCallback = null)
        {
            try
            {
                // Leer conexiones
                string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
                string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

                var readerDev = new DynamicsReader(connDev);
                var config = readerDev.GetConfigurationParameters();
                var readerProd = new DynamicsReader(connProd);

                // Fecha de corte
                int mesesConservar = int.Parse(config["months_to_keep"]);
                DateTime fechaCorte = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-mesesConservar);

                Logger.Log($"Iniciando proceso de extracción - (Anteriores a {fechaCorte:MMM yy})");

                // Entidades
                var entidades = new List<(string logicalName, int otc)>();
                int totalEntidades = int.Parse(config["total_entities"]);
                for (int i = 1; i <= totalEntidades; i++)
                {
                    entidades.Add((config[$"entity_{i}_logicalname"], int.Parse(config[$"entity_{i}_otc"])));
                }

                // ZIP Mode
                bool zipModeActivo = config["zip_upload_mode"].Equals("true", StringComparison.OrdinalIgnoreCase);

                // Componentes
                var processor = new AuditProcessor(readerProd.GetService());
                var exporter = new CsvExporter(readerProd.GetService(), "output", zipModeActivo);
                var uploader = new SharePointUploader(
                    config["sp_site"],
                    config["sp_upload_folder"],
                    config["sp_user"],
                    config["sp_password"]);

                string backupName;
                var bitacora = BitacoraManager.DownloadOrCreateBitacora(uploader, out backupName);
                Logger.Log("Bitácora local lista (descargada y respaldada)","",ConsoleColor.Magenta);

                var carpetasVerificadas = bitacora.GetVerifiedFolders();
                uploader.SetCarpetasVerificadas(carpetasVerificadas);

                var orquestador = new AuditOrchestrator(
                    readerProd, processor, exporter, uploader,
                    bitacora, backupName, entidades, fechaCorte, token);

                if (zipModeActivo)
                {
                    Logger.Log("🛠️ Modo de ejecución ZIP activado.", "", ConsoleColor.Cyan);
                    orquestador.EjecutarZip();
                }
                else
                {
                    Logger.Log("🛠️ Modo de ejecución Single activado.", "", ConsoleColor.Cyan);
                    orquestador.Ejecutar();
                }

                Logger.Log("Extracción de auditoría finalizada con éxito.","OK");
            }
            catch (OperationCanceledException)
            {
                Logger.Log("⏹️ Extracción pausada por señal externa.","WARN");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error fatal: {ex}","ERROR");
            }
        }
    }
}

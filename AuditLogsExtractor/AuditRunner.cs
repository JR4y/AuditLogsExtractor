using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;

namespace AuditLogsExtractor
{
    public class AuditRunner
    {
        #region Header Parameters Class
        public class HeaderParameters
        {
            public DateTime CutoffDate { get; set; }
            public bool ZipModeEnabled { get; set; }
            public string SharePointFolder { get; set; }
            public int TotalEntities { get; set; } // NUEVO
            public Dictionary<string, string> Configuration { get; set; }
        }
        #endregion

        #region Public Methods
        public HeaderParameters LoadHeaderParameters()
        {
            // Leer conexiones
            string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
            string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

            var reader = new DynamicsReader(connDev);
            var config = reader.GetConfigurationParameters();

            int monthsToKeep = int.Parse(config["months_to_keep"]);
            DateTime cutoffDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-monthsToKeep);

            bool zipMode = config["zip_upload_mode"].Equals("true", StringComparison.OrdinalIgnoreCase);
            string spFolder = config["sp_upload_folder"];
            int totalEntities = int.Parse(config["total_entities"]); // <-- NUEVO

            return new HeaderParameters
            {
                CutoffDate = cutoffDate,
                ZipModeEnabled = zipMode,
                SharePointFolder = spFolder,
                Configuration = config,
                TotalEntities = totalEntities // <-- NUEVO

            };
        }


        public void Execute(
            CancellationToken token,
            HeaderParameters parameters,
            Action<string> logCallback = null,
            Action<AuditOrchestrator.EstadoEntidadActual> stateCallback = null)
        {
            try
            {
                string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];
                var readerProd = new DynamicsReader(connProd);

                var entities = LoadEntitiesFromConfiguration(parameters.Configuration);

                var processor = new AuditProcessor(readerProd.GetService());
                var exporter = new CsvExporter(readerProd.GetService(), "output", parameters.ZipModeEnabled);
                var uploader = new SharePointUploader(
                    parameters.Configuration["sp_site"],
                    parameters.Configuration["sp_upload_folder"],
                    parameters.Configuration["sp_user"],
                    parameters.Configuration["sp_password"]
                );

                string backupName;
                var logManager = BitacoraManager.DownloadOrCreateBitacora(uploader, out backupName);
                Logger.Log("Bitácora local lista (descargada y respaldada)", "", ConsoleColor.DarkMagenta);

                var verifiedFolders = logManager.GetVerifiedFolders();
                uploader.SetVerifiedFolders(verifiedFolders);

                var orchestrator = new AuditOrchestrator(
                    readerProd,
                    processor,
                    exporter,
                    uploader,
                    logManager,
                    backupName,
                    entities,
                    parameters.CutoffDate,
                    token,
                    stateCallback
                );

                if (parameters.ZipModeEnabled)
                    orchestrator.EjecutarZip();
                else
                    orchestrator.Ejecutar();

                Logger.Log("Extracción de auditoría finalizada con éxito.", "OK");
            }
            catch (OperationCanceledException)
            {
                Logger.Log("⏹️ Extracción pausada por señal externa.", "WARN");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error fatal: {ex}", "ERROR");
            }
        }

        #endregion

        #region Helpers

        private List<(string logicalName, int otc)> LoadEntitiesFromConfiguration(Dictionary<string, string> config)
        {
            var entities = new List<(string logicalName, int otc)>();
            int totalEntities = int.Parse(config["total_entities"]);

            for (int i = 1; i <= totalEntities; i++)
            {
                entities.Add((
                    config[$"entity_{i}_logicalname"],
                    int.Parse(config[$"entity_{i}_otc"])
                ));
            }

            return entities;
        }

        #endregion

        /*public void Ejecutar(CancellationToken token, Action<string> logCallback = null, Action<string, string, string> cabeceraCallback = null, Action<AuditOrchestrator.EstadoEntidadActual> estadoCallback = null)
    {
        try
        {
            _cabeceraCallback = cabeceraCallback;

            // Leer conexiones
            string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
            string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

            var readerDev = new DynamicsReader(connDev);
            var config = readerDev.GetConfigurationParameters();
            var readerProd = new DynamicsReader(connProd);

            // Fecha de corte
            int mesesConservar = int.Parse(config["months_to_keep"]);
            DateTime fechaCorte = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-mesesConservar);

            //Logger.Log($"Iniciando proceso de extracción - (Anteriores a {fechaCorte:MMM yy})");

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


            //Asignar valores iniciales APP
            _cabeceraCallback?.Invoke(
                $"📅 Fecha corte: {fechaCorte:MMM yyyy}",
                $"⚙️ Modo: {(zipModeActivo ? "ZIP" : "Single")}",
                $"📂 SharePoint: {config["sp_upload_folder"]}" // ← asegúrate que sea público
            );

            string backupName;
            var bitacora = BitacoraManager.DownloadOrCreateBitacora(uploader, out backupName);
            Logger.Log("Bitácora local lista (descargada y respaldada)","",ConsoleColor.DarkMagenta);

            var carpetasVerificadas = bitacora.GetVerifiedFolders();
            uploader.SetVerifiedFolders(carpetasVerificadas);

            var orquestador = new AuditOrchestrator(
                readerProd, processor, exporter, uploader,
                bitacora, backupName, entidades, fechaCorte, token, estadoCallback);

            if (zipModeActivo)
            {
                orquestador.EjecutarZip();
            }
            else
            {
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
    }*/
}
}

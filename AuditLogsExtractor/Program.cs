using LiteDB;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            // Leer conexiones desde app.config
            string connDev = ConfigurationManager.AppSettings["D365_CONNECTION_DEV"];
            string connProd = ConfigurationManager.AppSettings["D365_CONNECTION_PROD"];

            // Inicializar lectores de configuración y servicio
            var readerDev = new DynamicsReader(connDev);
            var config = readerDev.GetConfigurationParameters();
            var readerProd = new DynamicsReader(connProd);

            // Definir fecha de corte
            int mesesConservar = int.Parse(config["months_to_keep"]);
            DateTime fechaCorte = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-mesesConservar);
            Logger.Info($"Iniciando proceso de extracción - (Anteriores a {fechaCorte:MMM yy})");

            // Cargar entidades desde configuración
            var entidades = new List<(string logicalName, int otc)>();
            int totalEntidades = int.Parse(config["total_entities"]);
            for (int i = 1; i <= totalEntidades; i++)
            {
                string logicalName = config[$"entity_{i}_logicalname"];
                int otc = int.Parse(config[$"entity_{i}_otc"]);
                entidades.Add((logicalName, otc));
            }

            //obtener parametro de zip
            bool zipModeActivo = config["zip_upload_mode"].Equals("true", StringComparison.OrdinalIgnoreCase);

            // Inicializar componentes
            var processor = new AuditProcessor(readerProd.GetService());
            //var exporter = new CsvExporter(readerProd.GetService(), zipModeActivo);
            var exporter = new CsvExporter(readerProd.GetService(), "output", zipModeActivo);
            var uploader = new SharePointUploader(
                config["sp_site"],
                config["sp_upload_folder"],
                config["sp_user"],
                config["sp_password"]);

            string backupName;
            var bitacora = BitacoraManager.DownloadOrCreateBitacora(uploader, out backupName);
            Logger.Info("Bitácora local lista (descargada y respaldada)", ConsoleColor.Magenta);

            var carpetasVerificadas = bitacora.GetVerifiedFolders();
            uploader.SetCarpetasVerificadas(carpetasVerificadas);

            // Preparar token de cancelación
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            FileSystemWatcher watcher = new FileSystemWatcher(Directory.GetCurrentDirectory(), "pause.signal")
            {
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    Console.WriteLine();
                    Logger.Warning("⚠️ Pausa detectada. Finalizando ejecución de forma segura...");
                    cts.Cancel();
                }
            };

            // Lanzar orquestador
            var orquestador = new AuditOrchestrator(
                readerProd,
                processor,
                exporter,
                uploader,
                bitacora,
                backupName,
                entidades,
                fechaCorte,
                token);

            if (zipModeActivo)
            {
                Logger.Info("🛠️ Modo de ejecución ZIP activado.", ConsoleColor.Cyan);
                orquestador.EjecutarZip();
            }
            else
            {
                orquestador.Ejecutar();
            }

            //orquestador.Ejecutar();
            Logger.Ok("Extracción de auditoría finalizada con éxito.");
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("⏹️ Extracción pausada por señal externa.");
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Error fatal: {ex}");
        }
    }
}
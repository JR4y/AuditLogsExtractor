using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;

namespace D365AuditExporter
{
    public class Runner
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
                TotalEntities = totalEntities
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
                var logManager = LogRepository.DownloadOrCreateBitacora(uploader, out backupName);
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

    }
}


/*
 * 20
 * https://auditlogsapi-ray-agemahbkhzekdhht.westeurope-01.azurewebsites.net/api/auditlogs
 * 
 * 6
 * 
 * 
 * https://ufinet.sharepoint.com/sites/billing
 * Documentos%20compartidos/AuditExport_TESTING
 * 
 * crm-pre@lyntia.com
 * LynT1a#21!
 * 
 * Entidad	Logical Name	Object Type Code	Habilitado
Acción de aplicación	appaction	10690	No
Agrupación Facturas	new_agrupacionfacturas	10369	No
Anexo permuta	new_anexocontrato	10006	No
Aprobación	msdyn_flow_approval	11497	No
Aprobación de Flow	msdyn_flow_flowapproval	11504	No
Cliente	account	1	No
Cobro Puntual Cliente	lyn_cobros_puntuales_cliente	10864	No
Cobros Puntuales	lyn_cobros_puntuales	10521	No
Configuración de facturación	new_configuraciondefacturacion	10373	No
Configuración de la aplicación basada en modelo	appsetting	10500	No
Configuración de la organización	organizationsetting	10621	No
Contacto	contact	2	No
Contrato marco	new_contratomarco	10010	No
Datos del modelo de aprobación básico	msdyn_flow_basicapprovalmodel	11503	No
Definición de parámetro	msdyn_productivityparameterdefinition	10580	No
Dirección	customeraddress	1071	No
Evento de flujo	flowevent	11464	No
Facturas	new_factura	10375	No
Gestión de Modificaciones	lyn_gestiondemodificaciones	11021	No
Identificador facturas	lyn_identificadorfacturas	10638	No
Imagen de máquina de flujo	flowmachineimage	10951	No
Línea de configuración de facturación	new_lineadeconfiguraciondefacturacion	10382	No
Línea de factura	new_lneadefactura	10383	No
Masking Rule	maskingrule	74	No
Oferta	quote	1084	Sí
Parámetro de entrada de acción	msdyn_productivityactioninputparameter	10574	No
Plantilla de acción de macro	msdyn_productivitymacroactiontemplate	10576	No
Producto de oferta	quotedetail	1085	No
Producto del proyecto	salesorderdetail	1089	No
Referencia	lead	4	No
Regla de acción de aplicación	appactionrule	10941	No
Respuesta de aprobación	msdyn_flow_approvalresponse	11499	No
Secuencias Facturación	new_secuenciasfacturacion	10390	No
Servicios contratados	new_servicioscontratados	10028	No
Solicitud de aprobación	msdyn_flow_approvalrequest	11498	No
Usuario	systemuser	8	No
*/
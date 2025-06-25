using System;
using System.Data.SQLite;
using System.IO;
using System.Threading;

namespace D365AuditExporter
{
    class Program
    {
        /*static void Main(string[] args)
        {
            //MigrarTablas();
        }*/

        static void Main(string[] args)
        {
            try
            {
                var cts = new CancellationTokenSource();

                // Pausa por archivo como antes
                FileSystemWatcher watcher = new FileSystemWatcher(Environment.CurrentDirectory, "pause.signal")
                {
                    EnableRaisingEvents = true
                };

                watcher.Created += (s, e) =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        Console.WriteLine();
                        Logger.Log("Pausa detectada. Finalizando ejecución de forma segura...", "WARN");
                        cts.Cancel();
                    }
                };
                Runner _runner = new Runner();
                _runner.Execute(cts.Token, _runner.LoadHeaderParameters());

            }
            catch (OperationCanceledException)
            {
                Logger.Log("Extracción pausada por señal externa.", "WARN");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error fatal: {ex}", "ERROR");
            }
        }

        public static void MigrarTablas()
        {
            Console.WriteLine("Iniciando migración de bitácora desde LiteDB a SQLite...");

            // Ruta del archivo de origen (LiteDB) y destino (SQLite)
            string liteDbPath = "bitacora.db";             // o donde esté ubicado realmente
            string sqlitePath = "bitacora.sqlite";         // destino de la nueva base SQLite

            try
            {
                var migrador = new LiteToSQLiteMigrator(liteDbPath, sqlitePath);
                migrador.EjecutarMigracion();

                Console.WriteLine("✅ Migración completada con éxito.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error durante la migración: {ex.Message}");
            }

            Console.WriteLine("Presiona cualquier tecla para salir...");
            Console.ReadKey();
        }
    }
}

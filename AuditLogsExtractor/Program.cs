using System;
using System.IO;
using System.Threading;

namespace AuditLogsExtractor
{
    class Program
    {
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
               AuditRunner _runner = new AuditRunner();
               _runner.Execute(cts.Token, _runner.LoadHeaderParameters());

            }
            catch (OperationCanceledException)
            {
                Logger.Log("Extracción pausada por señal externa.","WARN");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error fatal: {ex}","ERROR");
            }
        }
    }
}
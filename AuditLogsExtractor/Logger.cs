using System;

namespace AuditLogsExtractor
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static bool _lineaProgresoActiva = false;

        #region Info and Success

        public static void Info(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();
                Console.ForegroundColor = color;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO: {message}");
                Console.ResetColor();
            }
        }

        public static void Ok(string message)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ {message}");
                Console.ResetColor();
            }
        }

        #endregion

        #region Warnings and Errors

        public static void Warning(string message)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️  {message}");
                Console.ResetColor();
            }
        }

        public static void Error(string message)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ {message}");
                Console.ResetColor();
            }
        }

        public static void ErrorWithStack(string message, Exception ex)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ {message}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }
        }

        #endregion

        #region Progreso

        public static void Progreso(string entidad, int total, int totalActual, int procesados, int prevProcesados, int sinAuditoria, int errores)
        {
            lock (_lock)
            {
                double avance = total == 0 ? 100 : (double)totalActual / total * 100;
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write($"\r[{DateTime.Now:HH:mm:ss}] {entidad} >> {avance:0.#}% ({totalActual}/{total}) - [ Act:{procesados} | Prev:{prevProcesados} | S/Audit:{sinAuditoria} | Err:{errores} ]");
                Console.ResetColor();
                _lineaProgresoActiva = true;
            }
        }

        private static void LimpiarLineaProgresoSiEsNecesario()
        {
            if (_lineaProgresoActiva)
            {
                Console.Write("\r" + new string(' ', Console.WindowWidth) + "\r");
                _lineaProgresoActiva = false;
            }
        }

        public static void FinalizarLineaProgreso()
        {
            lock (_lock)
            {
                if (_lineaProgresoActiva)
                {
                    Console.WriteLine(); // Hace que el cursor baje a la siguiente línea
                    _lineaProgresoActiva = false;
                }
            }
        }

        #endregion
    }
}
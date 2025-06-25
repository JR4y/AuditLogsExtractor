using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace D365AuditExporter
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static bool _lineaProgresoActiva = false;
        public static Action<string, ConsoleColor?> ExternalLogger { get; set; } = null;
        public static Action<string> ExternalProgress { get; set; } = null;

        #region Info, Success, Warnings and Errors 

        public static void Log(string message, string prefix = "INFO", ConsoleColor? customColor = null, Exception ex = null)
        {
            lock (_lock)
            {
                LimpiarLineaProgresoSiEsNecesario();

                // Iconos e info visual
                string icono;
                ConsoleColor color;

                switch (prefix.ToUpperInvariant())
                {
                    case "OK":
                        icono = "✅";
                        color = ConsoleColor.Green;
                        break;
                    case "WARN":
                        icono = "⚠️";
                        color = ConsoleColor.Yellow;
                        break;
                    case "ERROR":
                        icono = "❌";
                        color = ConsoleColor.Red;
                        break;
                    default:
                        icono = "ℹ️";
                        color = ConsoleColor.Gray;
                        break;
                }

                // Si se especifica un color, usamos ese en lugar del predeterminado
                if (customColor.HasValue)
                    color = customColor.Value;

                string formatted = $"[{DateTime.Now:HH:mm:ss}] {prefix}: {icono} {message}";

                // Redirigir a WPF si aplica
                ExternalLogger?.Invoke(formatted, color);

                // Mostrar en consola solo si no hay redirección
                if (ExternalLogger == null)
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(formatted);
                    Console.ResetColor();
                }

                // Excepción adicional, solo para consola
                if (ex != null && ExternalLogger == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    Console.ResetColor();
                }
            }
        }

        #endregion

        #region Progreso

        public static void Progreso(string entidad, int total, int totalActual, int procesados, int prevProcesados, int sinAuditoria, int errores)
        {
            lock (_lock)
            {
                double avance = total == 0 ? 100 : (double)totalActual / total * 100;
                string formatted = $"[{DateTime.Now:HH:mm:ss}] {entidad} >> {avance:0.#}% ({totalActual}/{total}) - [ Act:{procesados} | Prev:{prevProcesados} | S/Audit:{sinAuditoria} | Err:{errores} ]";

                if (ExternalProgress != null)
                {
                    ExternalProgress.Invoke(formatted);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write("\r" + formatted);
                    Console.ResetColor();
                    _lineaProgresoActiva = true;
                }
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

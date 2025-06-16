using AuditLogsExtractor;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AuditLogsUI
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;
        private readonly AuditRunner _runner = new AuditRunner();
        private AuditRunner.HeaderParameters _loadedParameters;
        private int _entitiesProcessed = 0;
        public MainWindow()
        {
            InitializeComponent();

            Logger.ExternalLogger = (mensaje, color) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var paragraph = txtConsola.Document.Blocks.LastBlock as Paragraph;
                    if (paragraph == null)
                    {
                        paragraph = new Paragraph();
                        txtConsola.Document.Blocks.Add(paragraph);
                    }

                    var range = new Run(mensaje + Environment.NewLine)
                    {
                        Foreground = new SolidColorBrush(ConvertirColor(color ?? ConsoleColor.Gray))
                    };

                    paragraph.Inlines.Add(range);
                    txtConsola.ScrollToEnd();
                });
            };

            // Cargar parámetros iniciales
            _loadedParameters = _runner.LoadHeaderParameters();
            _entitiesProcessed = 0;

            MostrarCabecera(
                _loadedParameters.CutoffDate,
                _loadedParameters.ZipModeEnabled,
                _loadedParameters.SharePointFolder,
                _entitiesProcessed,
                _loadedParameters.TotalEntities
            );
        }

        private void btnIniciar_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();

            btnIniciar.IsEnabled = false;
            btnPausar.IsEnabled = true;
            txtConsola.Document.Blocks.Clear();

            Thread hilo = new Thread(() =>
            {
                try
                {
                    _runner.Execute(_cts.Token, _loadedParameters, LogDesdeUI, ActualizarEstadoEntidad);
                }
                catch (OperationCanceledException)
                {
                    LogDesdeUI("⏹️ Extracción pausada por el usuario.");
                }
                catch (Exception ex)
                {
                    LogDesdeUI($"❌ Error inesperado: {ex.Message}");
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        btnIniciar.IsEnabled = true;
                        btnPausar.IsEnabled = false;

                        btnIniciar.Tag = "activo";
                        btnPausar.Tag = "";
                    });
                }
            });

            hilo.IsBackground = true;
            hilo.Start();
        }
        private void btnPausar_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            btnPausar.Tag = "activo";
            btnIniciar.Tag = "";

            Logger.Log("Pausa solicitada desde la interfaz.", "WARN");
        }

        private void ActualizarEstadoEntidad(AuditOrchestrator.EstadoEntidadActual estado)
        {
            Dispatcher.Invoke(() =>
            {
                progressEntidad.Maximum = estado.Total;
                progressEntidad.Value = estado.Actual;

                MostrarCabecera(
                    _loadedParameters.CutoffDate,
                    _loadedParameters.ZipModeEnabled,
                    _loadedParameters.SharePointFolder,
                    estado.EntitiesCompleted,
                    _loadedParameters.TotalEntities
                );


                if (estado.ZIP) { 
                    lblEntidadActual.Text = $"Prefijo actual: '{estado.Prefijo}' ({estado.Actual}/{estado.Total}) - {((double)estado.Actual / Math.Max(1, estado.Total)):P0}";
                    txtResumenEntidad.Text = $"🟩 Entidad:{estado.Entidad} >> Exportados {estado.Exportados} - Sin audit {estado.SinAuditoria} - Errores {estado.Errores} - ⏱️ {estado.Duracion:hh\\:mm\\:ss}";
                }
                else
                {
                    lblEntidadActual.Text = $"Entidad actual: {estado.Entidad} ({estado.Actual}/{estado.Total}) - {((double)estado.Actual / Math.Max(1, estado.Total)):P0}";
                    txtResumenEntidad.Text = $"🟩 {estado.Entidad}: Exportados {estado.Exportados} - Sin audit {estado.SinAuditoria} - Previos {estado.Previos} - Errores {estado.Errores} - ⏱️ {estado.Duracion:hh\\:mm\\:ss}";
                }                                   
            });
        }
        private void MostrarCabecera(DateTime cutoff, bool zipMode, string destination, int processed, int total)
        {
            Dispatcher.Invoke(() =>
            {
                txtFechaCorte.Text = $"📅 Fecha Corte: {cutoff:MMM yyyy}";
                txtModoEjecucion.Text = $"⚙️ Modo: {(zipMode ? "ZIP" : "Single")}     Entidades: {processed}/{total} (por procesar)";
                txtSharePointDestino.Text = $"📂 SharePoint: {destination}";
            });
        }
        private void LogDesdeUI(string mensaje)
        {
            Dispatcher.Invoke(() =>
            {
                txtConsola.AppendText(mensaje + Environment.NewLine);
                txtConsola.ScrollToEnd();
            });
        }

        private Color ConvertirColor(ConsoleColor color)
        {
            switch (color)
            {
                case ConsoleColor.Black: return (Color)ColorConverter.ConvertFromString("#111827"); // Gris azulado profundo
                case ConsoleColor.DarkBlue: return (Color)ColorConverter.ConvertFromString("#1E3A8A"); // Azul oscuro nítido
                case ConsoleColor.DarkGreen: return (Color)ColorConverter.ConvertFromString("#065F46"); // Verde oscuro accesible
                case ConsoleColor.DarkCyan: return (Color)ColorConverter.ConvertFromString("#0E7490"); // Cian oscuro
                case ConsoleColor.DarkRed: return (Color)ColorConverter.ConvertFromString("#991B1B"); // Rojo ladrillo oscuro
                case ConsoleColor.DarkMagenta: return (Color)ColorConverter.ConvertFromString("#6B21A8"); // Violeta profundo
                case ConsoleColor.DarkYellow: return (Color)ColorConverter.ConvertFromString("#92400E"); // Mostaza intensa
                case ConsoleColor.Gray: return (Color)ColorConverter.ConvertFromString("#374151"); // Gris texto principal
                case ConsoleColor.DarkGray: return (Color)ColorConverter.ConvertFromString("#4B5563"); // Gris medio
                case ConsoleColor.Blue: return (Color)ColorConverter.ConvertFromString("#2563EB"); // Azul intenso (Tailwind Blue-600)
                case ConsoleColor.Green: return (Color)ColorConverter.ConvertFromString("#15803D"); // Verde accesible brillante
                case ConsoleColor.Cyan: return (Color)ColorConverter.ConvertFromString("#0EA5E9"); // Azul cielo vivo
                case ConsoleColor.Red: return (Color)ColorConverter.ConvertFromString("#DC2626"); // Rojo claro vivo
                case ConsoleColor.Magenta: return (Color)ColorConverter.ConvertFromString("#A855F7"); // Púrpura vivo
                case ConsoleColor.Yellow: return (Color)ColorConverter.ConvertFromString("#CA8A04"); // Amarillo mostaza legible
                case ConsoleColor.White: return (Color)ColorConverter.ConvertFromString("#111827"); // Casi negro para máxima legibilidad
                default: return (Color)ColorConverter.ConvertFromString("#374151"); // Gris neutral por defecto
            }
        }
    }
}
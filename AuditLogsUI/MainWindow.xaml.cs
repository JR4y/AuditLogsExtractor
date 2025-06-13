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

        public MainWindow()
        {
            InitializeComponent();

            // Redirigir los mensajes del logger a la consola WPF
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

            Logger.ExternalProgress = progreso =>
            {
                Dispatcher.Invoke(() =>
                {
                    txtProgreso.Text = progreso;
                });
            };
        }

        private void btnIniciar_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();

            btnIniciar.IsEnabled = false;
            btnPausar.IsEnabled = true;

            Thread hilo = new Thread(() =>
            {
                try
                {
                    _runner.Ejecutar(_cts.Token, LogDesdeUI);
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
                        txtProgreso.Text = string.Empty;
                        btnIniciar.IsEnabled = true;
                        btnPausar.IsEnabled = false;
                    });
                }
            });

            hilo.IsBackground = true;
            hilo.Start();
        }

        private void btnPausar_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            txtProgreso.Text = string.Empty;
            Logger.Log("Pausa solicitada desde la interfaz.", "WARN");
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
                case ConsoleColor.Black: return (Color)ColorConverter.ConvertFromString("#1C1C1C");
                case ConsoleColor.DarkBlue: return (Color)ColorConverter.ConvertFromString("#2C3E50");
                case ConsoleColor.DarkGreen: return (Color)ColorConverter.ConvertFromString("#196F3D");
                case ConsoleColor.DarkCyan: return (Color)ColorConverter.ConvertFromString("#117A65");
                case ConsoleColor.DarkRed: return (Color)ColorConverter.ConvertFromString("#922B21");
                case ConsoleColor.DarkMagenta: return (Color)ColorConverter.ConvertFromString("#7D3C98");
                case ConsoleColor.DarkYellow: return (Color)ColorConverter.ConvertFromString("#B7950B"); // Equiv. a "DarkGoldenrod"
                case ConsoleColor.Gray: return (Color)ColorConverter.ConvertFromString("#BFC9CA"); // Suave, no blanco
                case ConsoleColor.DarkGray: return (Color)ColorConverter.ConvertFromString("#7F8C8D");
                case ConsoleColor.Blue: return (Color)ColorConverter.ConvertFromString("#3498DB"); // Azul elegante
                case ConsoleColor.Green: return (Color)ColorConverter.ConvertFromString("#2ECC71"); // Verde éxito
                case ConsoleColor.Cyan: return (Color)ColorConverter.ConvertFromString("#1ABC9C");
                case ConsoleColor.Red: return (Color)ColorConverter.ConvertFromString("#E74C3C"); // Rojo moderno
                case ConsoleColor.Magenta: return (Color)ColorConverter.ConvertFromString("#9B59B6"); // Violeta moderno
                case ConsoleColor.Yellow: return (Color)ColorConverter.ConvertFromString("#F1C40F"); // Amarillo vivo
                case ConsoleColor.White: return (Color)ColorConverter.ConvertFromString("#ECF0F1"); // Blanco suave
                default: return (Color)ColorConverter.ConvertFromString("#BDC3C7"); // Gris neutro
            }
        }
    }
}
using System;
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using AuditLogsExtractor;

namespace AuditLogsUI
{
    public class ConsoleWriter : TextWriter
    {
        private readonly TextBox _output;
        private readonly Dispatcher _dispatcher;

        public ConsoleWriter(TextBox output)
        {
            _output = output;
            _dispatcher = output.Dispatcher;
        }

        public override void Write(char value)
        {
            _dispatcher.Invoke(() => _output.AppendText(value.ToString()));
        }

        public override void Write(string value)
        {
            _dispatcher.Invoke(() => _output.AppendText(value));
        }

        public override void WriteLine(string value)
        {
            _dispatcher.Invoke(() =>
            {
                _output.AppendText(value + Environment.NewLine);
                _output.ScrollToEnd();
            });
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
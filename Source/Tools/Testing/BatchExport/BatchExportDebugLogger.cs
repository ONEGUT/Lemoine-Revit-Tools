// TEMPORARY DEBUG — remove before release once NWC/IFC export is stable
using System;
using System.IO;

namespace LemoineTools.Tools.Testing
{
    internal sealed class BatchExportDebugLogger : IDisposable
    {
        private StreamWriter? _w;
        private bool _disposed;

        internal BatchExportDebugLogger(string outputFolder)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(outputFolder, $"BatchExportDebug_{timestamp}.log");
                _w = new StreamWriter(path, append: false) { AutoFlush = true };
                _w.WriteLine($"=== Batch Export Debug Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                _w.WriteLine($"Machine: {Environment.MachineName}  User: {Environment.UserName}");
                _w.WriteLine($"CLR: {Environment.Version}  OS: {Environment.OSVersion}");
                _w.WriteLine();
            }
            catch { /* never crash over a debug file */ }
        }

        internal void Log(string category, string message, string? detail = null)
        {
            if (_disposed || _w == null) return;
            try
            {
                _w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{category,-16}] {message}");
                if (!string.IsNullOrEmpty(detail))
                {
                    foreach (string line in detail.Split('\n'))
                        _w.WriteLine($"                              |  {line.TrimEnd()}");
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _w?.WriteLine();
                _w?.WriteLine("=== End of log ===");
                _w?.Dispose();
            }
            catch { }
        }
    }
}

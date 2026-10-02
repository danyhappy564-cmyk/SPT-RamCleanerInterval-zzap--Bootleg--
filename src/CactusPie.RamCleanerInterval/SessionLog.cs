using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Copies every line this plugin logs into its own file, one per game session:
    /// BepInEx\RamCleaner\RamCleaner-yyyyMMdd-HHmmss.log (the newest 15 are kept). LogOutput.log still gets
    /// everything too; this file is just the RAM cleaner part, easy to read or send on its own.
    /// </summary>
    internal sealed class SessionLog : ILogListener
    {
        private const int KeepFiles = 15;

        private readonly ILogSource _source;
        private readonly object _gate = new object();
        private StreamWriter _writer;

        public SessionLog(ILogSource source)
        {
            _source = source;
            try
            {
                string folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "RamCleaner");
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, $"RamCleaner-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
                _writer.WriteLine($"# RAM cleaner session log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                foreach (string old in Directory.GetFiles(folder, "RamCleaner-*.log").OrderByDescending(f => f).Skip(KeepFiles))
                {
                    File.Delete(old);
                }
            }
            catch (Exception)
            {
                _writer = null;
                FilePath = null;
            }
        }

        public string FilePath { get; private set; }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (_writer == null || eventArgs.Source != _source)
            {
                return;
            }

            Write($"{DateTime.Now:HH:mm:ss} [{eventArgs.Level}] {eventArgs.Data}");
        }

        /// <summary>Writes a block (diagnostic start/stop marker, summary) without a log level prefix.</summary>
        public void WriteBlock(string text)
        {
            Write(text);
        }

        private void Write(string line)
        {
            lock (_gate)
            {
                try
                {
                    _writer?.WriteLine(line);
                }
                catch (Exception)
                {
                    _writer = null;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}

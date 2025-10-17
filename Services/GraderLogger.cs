using System;
using System.IO;
using System.Text;

namespace SolutionGrader.Services
{
    public static class GraderLogger
    {
        private static readonly object _lock = new();
        private static readonly string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static readonly string _logFile = Path.Combine(_logDirectory, "solutiongrader.log");

        static GraderLogger()
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch
            {
                // ignore directory creation errors
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warning(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null)
        {
            var builder = new StringBuilder(message);
            if (ex != null)
            {
                builder.AppendLine();
                builder.Append(ex.ToString());
            }

            Write("ERROR", builder.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                var line = $"{DateTime.Now:O} [{level}] {message}{Environment.NewLine}";
                lock (_lock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
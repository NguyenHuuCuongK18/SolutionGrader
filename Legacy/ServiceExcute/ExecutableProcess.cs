using System.Diagnostics;
using System.IO;
using SolutionGrader.Legacy.Service;

namespace SolutionGrader.Legacy.ServiceExcute
{
    public class ExecutableProcess
    {
        public string FilePath { get; }

        private Process? _process;

        public ExecutableProcess(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Executable not found: {filePath}", filePath);

            FilePath = filePath;
        }

        /// <summary>
        /// Start the process.
        /// </summary>
        public void Start()
        {
            if (_process != null && !_process.HasExited)
                throw new InvalidOperationException("Process is already running.");

            var startInfo = new ProcessStartInfo
            {
                FileName = FilePath,
                UseShellExecute = true,   // để mở exe bình thường
                CreateNoWindow = false
            };

            _process = Process.Start(startInfo);
        }

        /// <summary>
        /// Stop the process if it is running.
        /// </summary>
        public void Stop()
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process.Dispose();
                _process = null;
            }
        }

        /// <summary>
        /// Check if process is running.
        /// </summary>
        public bool IsRunning => _process != null && !_process.HasExited;
    }
}

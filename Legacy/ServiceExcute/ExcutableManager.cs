using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SolutionGrader.Legacy.Service;

namespace SolutionGrader.Legacy.ServiceExcute
{
    public class ExecutableManager
    {
        private static readonly Lazy<ExecutableManager> _instance =
            new(() => new ExecutableManager());
        public static ExecutableManager Instance => _instance.Value;

        public Process? _clientProcess;
        public Process? _serverProcess;

        public event Action<string>? ClientOutputReceived;
        public event Action<string>? ServerOutputReceived;

        public bool IsServerRunning => _serverProcess != null && !_serverProcess.HasExited;
        public bool IsClientRunning => _clientProcess != null && !_clientProcess.HasExited;

        private readonly string _debugFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_logs");

        public ExecutableManager()
        {
            Directory.CreateDirectory(_debugFolder);
        }

        public void Init(string clientPath, string serverPath)
        {
            _pendingClientPath = clientPath;
            _pendingServerPath = serverPath;
            InitClient(clientPath);
            InitServer(serverPath);
        }

        private Process CreateProcess(string exePath, Action<string> onOutput, string role)
        {
            if (!File.Exists(exePath))
                throw new FileNotFoundException($"Executable not found: {exePath}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.Exited += (s, e) => onOutput($"[{role}] exited.");
            return process;
        }

        #region Start/Stop (foreground-less)

        // ExecutableManager.cs  (additions)
        public void InitClient(string clientPath)
        {
            _clientProcess = CreateProcess(clientPath, msg =>
            {
                ClientOutputReceived?.Invoke(msg);
                AppendDebugFile("client.log", msg);
            }, "Client");
        }

        public void InitServer(string serverPath)
        {
            _serverProcess = CreateProcess(serverPath, msg =>
            {
                ServerOutputReceived?.Invoke(msg);
                AppendDebugFile("server.log", msg);
            }, "Server");
        }

        public void StartServer()
        {
            if (_serverProcess == null)
            {
                if (string.IsNullOrWhiteSpace(_pendingServerPath))
                    throw new InvalidOperationException("Server process not initialized.");
                InitServer(_pendingServerPath);
            }
            StartProcessAndMonitor(_serverProcess!, msg => ServerOutputReceived?.Invoke(msg), "server.log");
        }

        public void StartClient()
        {
            if (_clientProcess == null)
            {
                if (string.IsNullOrWhiteSpace(_pendingClientPath))
                    throw new InvalidOperationException("Client process not initialized.");
                InitClient(_pendingClientPath);
            }
            StartProcessAndMonitor(_clientProcess!, msg => ClientOutputReceived?.Invoke(msg), "client.log");
        }

        // store paths captured during Init so we can reinit later
        private string? _pendingClientPath;
        private string? _pendingServerPath;




        public void StartBoth()
        {
            StartServer();
            StartClient();
        }

        private void StartProcessAndMonitor(Process process, Action<string> onOutput, string logFile)
        {
            process.Start();

            // ===== STDOUT =====
            Task.Run(async () =>
            {
                var reader = process.StandardOutput;
                char[] buffer = new char[1024];
                int read;

                var sb = new StringBuilder();
                object sbLock = new object();
                System.Threading.CancellationTokenSource? pendingFlushCts = null;
                const int DEBOUNCE_MS = 100;

                void ScheduleFlushPartial()
                {
                    var prev = System.Threading.Interlocked.Exchange(ref pendingFlushCts, new System.Threading.CancellationTokenSource());
                    if (prev != null) { try { prev.Cancel(); prev.Dispose(); } catch { } }

                    var cts = pendingFlushCts;
                    _ = Task.Run(async () =>
                    {
                        try { await Task.Delay(DEBOUNCE_MS, cts!.Token); }
                        catch (TaskCanceledException) { return; }

                        string partial;
                        lock (sbLock)
                        {
                            if (sb.Length == 0) return;
                            partial = sb.ToString();
                            sb.Clear();
                        }

                        onOutput(partial);
                        AppendDebugFile(logFile, partial);
                    });
                }

                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            string line;
                            lock (sbLock) { line = sb.ToString(); sb.Clear(); }
                            if (!string.IsNullOrEmpty(line))
                            {
                                onOutput(line);
                                AppendDebugFile(logFile, line);
                            }

                            var prev = System.Threading.Interlocked.Exchange(ref pendingFlushCts, null);
                            if (prev != null) { try { prev.Cancel(); prev.Dispose(); } catch { } }
                        }
                        else
                        {
                            lock (sbLock) sb.Append(c);
                        }
                    }

                    bool hasPartial;
                    lock (sbLock) { hasPartial = sb.Length > 0; }
                    if (hasPartial) ScheduleFlushPartial();
                }

                var finalCts = System.Threading.Interlocked.Exchange(ref pendingFlushCts, null);
                if (finalCts != null) { try { finalCts.Cancel(); finalCts.Dispose(); } catch { } }

                string? last;
                lock (sbLock) { last = sb.Length > 0 ? sb.ToString() : null; sb.Clear(); }
                if (!string.IsNullOrEmpty(last))
                {
                    onOutput(last);
                    AppendDebugFile(logFile, last);
                }
            });

            // ===== STDERR =====
            Task.Run(async () =>
            {
                var errReader = process.StandardError;
                char[] errBuf = new char[1024];
                int errRead;
                var errSb = new StringBuilder();

                while ((errRead = await errReader.ReadAsync(errBuf, 0, errBuf.Length)) > 0)
                {
                    for (int i = 0; i < errRead; i++)
                    {
                        char c = errBuf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (errSb.Length > 0)
                            {
                                var chunk = errSb.ToString();
                                errSb.Clear();
                                onOutput("[ERR] " + chunk);
                                AppendDebugFile(logFile, "[ERR] " + chunk);
                            }
                        }
                        else
                        {
                            errSb.Append(c);
                        }
                    }

                    if (errSb.Length > 0)
                    {
                        var partial = errSb.ToString();
                        errSb.Clear();
                        onOutput("[ERR] " + partial);
                        AppendDebugFile(logFile, "[ERR] " + partial);
                    }
                }

                if (errSb.Length > 0)
                {
                    var leftover = errSb.ToString();
                    onOutput("[ERR] " + leftover);
                    AppendDebugFile(logFile, "[ERR] " + leftover);
                }
            });
        }

        public void StopAll()
        {
            StopProcess(ref _clientProcess);
            StopProcess(ref _serverProcess);
        }

        private void StopProcess(ref Process? process)
        {
            if (process == null) return;

            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill(true);
                        process.WaitForExit();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StopProcess ERR] {ex.Message}");
            }
            finally
            {
                try { process.Dispose(); } catch { }
                process = null;
            }
        }
        #endregion

        #region Input/Output helpers

        public void SendClientInput(string input)
        {
            if (_clientProcess != null && !_clientProcess.HasExited)
                _clientProcess.StandardInput.WriteLine(input);
        }

        private void AppendDebugFile(string fileName, string text)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(_debugFolder, fileName),
                    $"{DateTime.Now:O} {text}{Environment.NewLine}"
                );
            }
            catch { }
        }

        #endregion

        #region Async stop API (silent, no UI)

        public async Task StopAllAsync()
        {
            var stopClientTask = StopProcessAsync(_clientProcess, "Client");
            var stopServerTask = StopProcessAsync(_serverProcess, "Server");

            await Task.WhenAll(stopClientTask, stopServerTask);

            _clientProcess = null;
            _serverProcess = null;
        }

        public async Task StopClientAsync()
        {
            await StopProcessAsync(_clientProcess, "Client");
            _clientProcess = null;
        }

        public async Task StopServerAsync()
        {
            await StopProcessAsync(_serverProcess, "Server");
            _serverProcess = null;
        }

        private async Task StopProcessAsync(Process? process, string role)
        {
            if (process == null) return;

            int processId;
            try
            {
                if (process.HasExited) return;
                processId = process.Id;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }

            await Task.Run(() =>
            {
                try
                {
                    using var taskKillProcess = new Process();
                    var startInfo = taskKillProcess.StartInfo;
                    startInfo.FileName = "taskkill";
                    startInfo.Arguments = $"/F /PID {processId} /T";
                    startInfo.UseShellExecute = false;
                    startInfo.CreateNoWindow = true;

                    taskKillProcess.Start();
                    taskKillProcess.WaitForExit(3000);

                    AppendDebugFile($"{role.ToLower()}.log",
                        $"[{role}] Termination command sent to process ID {processId} via taskkill.");
                }
                catch (Exception ex)
                {
                    AppendDebugFile($"{role.ToLower()}.log",
                        $"[StopProcess ERR] Exception during taskkill: {ex}");
                }
            });
        }

        #endregion
    }
}

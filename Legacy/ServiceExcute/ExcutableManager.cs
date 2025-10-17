using System;
using System.Diagnostics;
using System.IO;
using System.Security.RightsManagement;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using SolutionGrader.Legacy.Service;
using SolutionGrader.Legacy.Views;

namespace SolutionGrader.Legacy.ServiceExcute  
{
    public class ExecutableManager
    {
        private static readonly Lazy<ExecutableManager> _instance =
            new(() => new ExecutableManager());
        public static ExecutableManager Instance => _instance.Value;
        private Process? _clientProcess;
        private Process? _serverProcess;

        public event Action<string>? ClientOutputReceived;
        public event Action<string>? ServerOutputReceived;

        private readonly string _debugFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "process_logs");

        public ExecutableManager()
        {
            Directory.CreateDirectory(_debugFolder);
        }
        //load list ignore
        //public void InitializeIgnoreList(string excelPath)
        //{
        //    try
        //    {
        //        var file = Path.Combine("D:\\CSharp_Project\\TestKitGenerator", "Ignore.xlsx");
        //        _ignoreTexts = IgnoreListLoader.IgnoreLoader(file);
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show($"Không thể load file ignore: {ex.Message}");
        //        _ignoreTexts = new HashSet<string>();
        //    }
        //}
        // method check isIgnore
        //private bool ShouldIgnore(string line)
        //{
        //    if (_ignoreTexts == null || _ignoreTexts.Count == 0)
        //        return false;

        //    foreach (var ignore in _ignoreTexts)
        //    {
        //        if (line.Contains(ignore, StringComparison.OrdinalIgnoreCase))
        //            return true;
        //    }
        //    return false;
        //}


        /// <summary>
        /// Khởi tạo sẵn thông tin process mà chưa chạy.
        /// </summary>
        public void Init(string clientPath, string serverPath)
        {
            _clientProcess = CreateProcess(clientPath, msg =>
            {
                ClientOutputReceived?.Invoke(msg);
                AppendDebugFile("client.log", msg);
            }, "Client");

            _serverProcess = CreateProcess(serverPath, msg =>
            {
                ServerOutputReceived?.Invoke(msg);
                AppendDebugFile("server.log", msg);
            }, "Server");
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

        #region Start/Stop

        /// <summary>
        /// Chạy server trước để middleware có thể kết nối.
        /// </summary>
        public void StartServer()
        {
            if (_serverProcess == null)
                throw new InvalidOperationException("Server process not initialized.");

            StartProcessAndMonitor(_serverProcess, msg => ServerOutputReceived?.Invoke(msg), "server.log");
        }

        /// <summary>
        /// Chạy client sau khi middleware đã sẵn sàng.
        /// </summary>
        public void StartClient()
        {
            if (_clientProcess == null)
                throw new InvalidOperationException("Client process not initialized.");

            StartProcessAndMonitor(_clientProcess, msg => ClientOutputReceived?.Invoke(msg), "client.log");
        }

        /// <summary>
        /// Trước đây là StartBoth, giữ lại nếu cần chạy song song.
        /// </summary>
        public void StartBoth()
        {
            StartServer();
            StartClient();
        }

        private void StartProcessAndMonitor(Process process, Action<string> onOutput, string logFile)
        {
            process.Start();

            // ===== OUTPUT (stdout) =====
            Task.Run(async () =>
            {
                var reader = process.StandardOutput;
                char[] buffer = new char[1024];
                int read;

                var sb = new StringBuilder();      // lưu partial line giữa các chunk
                object sbLock = new object();
                CancellationTokenSource pendingFlushCts = null;

                const int DEBOUNCE_MS = 100; // thời gian chờ trước khi flush phần partial (tùy chỉnh)

                void ScheduleFlushPartial()
                {
                    // Cancel + dispose cts trước đó (nếu có)
                    var prev = Interlocked.Exchange(ref pendingFlushCts, new CancellationTokenSource());
                    if (prev != null)
                    {
                        try { prev.Cancel(); prev.Dispose(); }
                        catch { }
                    }

                    var cts = pendingFlushCts;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(DEBOUNCE_MS, cts.Token);
                        }
                        catch (TaskCanceledException) { return; }

                        string partial;
                        lock (sbLock)
                        {
                            if (sb.Length == 0) return;
                            partial = sb.ToString();
                            sb.Clear();
                        }

                        // flush partial (đã có pause -> coi như hoàn chỉnh)
                        onOutput(partial);
                        AppendDebugFile(logFile, partial);
                    });
                }

                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];

                        // coi cả '\r' và '\n' là terminator
                        if (c == '\r' || c == '\n')
                        {
                            string line;
                            lock (sbLock)
                            {
                                line = sb.ToString();
                                sb.Clear();
                            }

                            if (!string.IsNullOrEmpty(line))
                            {
                                onOutput(line);
                                AppendDebugFile(logFile, line);
                            }

                            // Nếu có timer flush partial đang chờ, huỷ nó (bởi ta vừa flush)
                            var prev = Interlocked.Exchange(ref pendingFlushCts, null);
                            if (prev != null)
                            {
                                try { prev.Cancel(); prev.Dispose(); }
                                catch { }
                            }
                        }
                        else
                        {
                            lock (sbLock) sb.Append(c);
                        }
                    }

                    // Nếu còn partial (không có newline trong chunk), schedule một flush sau debounce
                    bool hasPartial;
                    lock (sbLock) { hasPartial = sb.Length > 0; }
                    if (hasPartial) ScheduleFlushPartial();
                }

                // Khi stream kết thúc: huỷ timer và flush phần còn lại ngay
                var finalCts = Interlocked.Exchange(ref pendingFlushCts, null);
                if (finalCts != null) { try { finalCts.Cancel(); finalCts.Dispose(); } catch { } }

                string last;
                lock (sbLock)
                {
                    last = sb.Length > 0 ? sb.ToString() : null;
                    sb.Clear();
                }
                if (!string.IsNullOrEmpty(last))
                {
                    onOutput(last);
                    AppendDebugFile(logFile, last);
                }
            });

            // ===== ERROR (stderr) =====
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

                    // error thường ngắn — flush partial ngay
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
                process.Dispose();
                process = null;
            }
        }
        #endregion

        #region Input/Output

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
        #region StopAllAsync
        public async Task StopAllAsync()
        {
            ProgressDialog? dialog = null;
            try
            {
                // Hiển thị loading dialog không chặn
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new ProgressDialog("Đang dừng tất cả tiến trình...");
                    dialog.Owner = Application.Current.MainWindow;
                    dialog.Show(); // Không dùng ShowDialog -> không chặn luồng
                });

                // Dừng tiến trình song song
                var stopClientTask = StopProcessAsync(_clientProcess, "Client");
                var stopServerTask = StopProcessAsync(_serverProcess, "Server");

                await Task.WhenAll(stopClientTask, stopServerTask);

                _clientProcess = null;
                _serverProcess = null;

                // Đóng dialog sau khi dừng xong
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show("Tất cả tiến trình đã được dừng thành công.",
                        "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show($"[StopAllAsync ERR] {ex.Message}",
                        "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }
        #endregion

        #region StopClientAsync
        public async Task StopClientAsync()
        {
            ProgressDialog? dialog = null;
            try
            {
                // Hiển thị loading dialog không chặn
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new ProgressDialog("Đang dừng tiến trình client...");
                    dialog.Owner = Application.Current.MainWindow;
                    dialog.Show(); // Không dùng ShowDialog -> không chặn luồng
                });

                await StopProcessAsync(_clientProcess, "Client");

                _clientProcess = null;

                // Đóng dialog sau khi dừng xong
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show("Tiến trình client đã được dừng thành công.",
                        "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show($"[StopClientAsync ERR] {ex.Message}",
                        "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }
        #endregion

        #region StopServerAsync
        public async Task StopServerAsync()
        {
            ProgressDialog? dialog = null;
            try
            {
                // Hiển thị loading dialog không chặn
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new ProgressDialog("Đang dừng tiến trình server...");
                    dialog.Owner = Application.Current.MainWindow;
                    dialog.Show(); // Không dùng ShowDialog -> không chặn luồng
                });

                await StopProcessAsync(_serverProcess, "Server");

                _serverProcess = null;

                // Đóng dialog sau khi dừng xong
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show("Tiến trình server đã được dừng thành công.",
                        "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                    MessageBox.Show($"[StopServerAsync ERR] {ex.Message}",
                        "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }
        #endregion

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

            // Chuyển sang một luồng nền để thực thi lệnh taskkill mà không làm treo UI.
            await Task.Run(() =>
            {
                try
                {
                    // Tạo một process mới chỉ để chạy lệnh taskkill
                    using (var taskKillProcess = new Process())
                    {
                        var startInfo = taskKillProcess.StartInfo;
                        startInfo.FileName = "taskkill";
                        // /F: Buộc dừng (Force)
                        // /T: Dừng cả các process con (Tree)
                        // /PID: Dừng theo Process ID
                        startInfo.Arguments = $"/F /PID {processId} /T";
                        startInfo.UseShellExecute = false;
                        startInfo.CreateNoWindow = true; // Chạy ẩn, không hiện cửa sổ cmd

                        taskKillProcess.Start();
                        taskKillProcess.WaitForExit(3000); 

                        AppendDebugFile($"{role.ToLower()}.log", $"[{role}] Termination command sent to process ID {processId} via taskkill.");
                    }
                }
                catch (Exception ex)
                {
                    AppendDebugFile($"{role.ToLower()}.log", $"[StopProcess ERR] Exception during taskkill: {ex}");
                }
            });
        }
        #endregion
    }
}
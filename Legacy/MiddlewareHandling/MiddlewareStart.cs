using SolutionGrader.Legacy.Model;
using SolutionGrader.Legacy.Recorder;
using SolutionGrader.Legacy.Service;
using SolutionGrader.Legacy.Views;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// NOTE: do not import System.Windows.Forms; qualify WPF Application usage

namespace SolutionGrader.Legacy.MiddlewareHandling
{
    public sealed class MiddlewareStart
    {
        private static readonly Lazy<MiddlewareStart> _instance =
            new(() => new MiddlewareStart());
        public static MiddlewareStart Instance => _instance.Value;
        private MiddlewareStart() { }

        public IRecorderContext? Recorder { get; set; }

        private CancellationTokenSource? _cts;
        private bool _isSessionRunning;

        private HttpListener? _httpListener;
        private TcpListener? _tcpListener;

        private const int ProxyPort = 5000;
        private const int RealServerPort = 5001;

        public async Task StartAsync(bool useHttp = true)
        {
            if (_isSessionRunning)
                return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _isSessionRunning = true;

            if (useHttp)
            {
                StartHttpProxy(token);
            }
            else
            {
                StartTcpProxy(token);
            }

            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            ProgressDialog? dialog = null;
            try
            {
                if (!_isSessionRunning) return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog = new ProgressDialog("Đang dừng Middleware Proxy...");
                    dialog.Owner = System.Windows.Application.Current.MainWindow;
                    dialog.Show();
                });

                _isSessionRunning = false;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                    _httpListener = null;
                }

                if (_tcpListener != null)
                {
                    _tcpListener.Stop();
                    _tcpListener = null;
                }

                AppendToFile(new LoggedRequest
                {
                    Method = "SYSTEM",
                    Url = "Proxy stopped",
                    RequestBody = "Middleware stopped gracefully.",
                    StatusCode = 0
                });

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                });
            }
            catch (Exception)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog?.Close();
                });
            }
        }

        private void StartHttpProxy(CancellationToken token)
        {
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{ProxyPort}/");
                _httpListener.Start();

                Task.Run(() => ListenForHttpRequests(token), token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HTTP Proxy ERR] {ex.Message}");
            }
        }

        private async Task ListenForHttpRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener!.GetContextAsync();
                    _ = Task.Run(() => ProcessHttpRequest(context), token);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Middleware ERR] {ex.Message}");
                }
            }
        }

        private async Task ProcessHttpRequest(HttpListenerContext context)
        {
            var request = context.Request;
            string requestBody;

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            requestBody = reader.ReadToEnd();

            var requestBytes = request.ContentEncoding.GetBytes(requestBody);
            var requestDataType = DataInspector.DetecDataType(requestBytes);
            if (Recorder != null && Recorder.InputClients.Any())
            {
                var stage = Recorder.InputClients.Last().Stage;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Recorder.OutputServers.Add(new OutputServer
                    {
                        Stage = stage,
                        Method = request.HttpMethod,
                        DataTypeMiddleware = requestDataType,
                        ByteSize = requestBytes.Length,
                        DataRequest = requestBody,
                    });
                });
            }

            try
            {
                var realServerUrl = $"http://localhost:{RealServerPort}{request.Url?.AbsolutePath}";
                using var client = new HttpClient();
                var forwardRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), realServerUrl);
                MediaTypeHeaderValue? contentType = null;
                if (request.ContentType != null) { contentType = MediaTypeHeaderValue.Parse(request.ContentType); }
                forwardRequest.Content = new StringContent(requestBody, contentType);

                var responseMessage = await client.SendAsync(forwardRequest);
                var responseBytes = await responseMessage.Content.ReadAsByteArrayAsync();
                string responseBody = Encoding.UTF8.GetString(responseBytes);
                var responseDataType = DataInspector.DetecDataType(responseBytes);

                if (Recorder != null && Recorder.InputClients.Any())
                {
                    var stage = Recorder.InputClients.Last().Stage;
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Recorder.OutputClients.Add(new OutputClient
                        {
                            Stage = stage,
                            Method = request.HttpMethod,
                            DataTypeMiddleWare = responseDataType,
                            ByteSize = responseBytes.Length,
                            StatusCode = (int)responseMessage.StatusCode,
                            DataResponse = responseBody,
                        });
                    });
                }

                var response = context.Response;
                response.StatusCode = (int)responseMessage.StatusCode;
                response.ContentType = responseMessage.Content.Headers.ContentType?.ToString();
                response.ContentLength64 = responseBytes.Length;

                await response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                AppendToFile(new LoggedRequest
                {
                    Method = request.HttpMethod,
                    Url = request.Url?.ToString() ?? "Unknown",
                    RequestBody = ex.Message,
                    StatusCode = -1
                });
            }
        }

        private void StartTcpProxy(CancellationToken token)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, ProxyPort);
                _tcpListener.Start();

                Task.Run(() => ListenForTcpConnections(token), token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP Proxy ERR] {ex.Message}");
            }
        }

        private async Task ListenForTcpConnections(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _tcpListener!.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleTcpConnection(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TCP Accept ERR] {ex.Message}");
                }
            }
        }

        private async Task HandleTcpConnection(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (var server = new TcpClient())
                {
                    await server.ConnectAsync(IPAddress.Loopback, RealServerPort, token);
                    using var clientStream = client.GetStream();
                    using var serverStream = server.GetStream();

                    var c2s = RelayDataAsync(clientStream, serverStream, "Client", token);
                    var s2c = RelayDataAsync(serverStream, clientStream, "Server", token);

                    await Task.WhenAny(c2s, s2c);
                }
            }
            catch (Exception ex)
            {
                var log = new LoggedRequest
                {
                    Method = "TCP",
                    Url = "Connection Error",
                    RequestBody = ex.Message,
                    StatusCode = -1
                };
                AddRequestLog(log);
            }
        }

        private async Task RelayDataAsync(NetworkStream from, NetworkStream to, string direction, CancellationToken token)
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await from.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                await to.WriteAsync(buffer, 0, read, token);
                var data = Encoding.UTF8.GetString(buffer, 0, read);
                var dataType = DataInspector.DetecDataType(buffer.Take(read).ToArray());
                var byteSize = read;

                if (Recorder != null && Recorder.InputClients.Any())
                {
                    var stage = Recorder.InputClients.Last().Stage;
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (direction.Contains("Client"))
                        {
                            var existingOutputServer = Recorder.OutputServers.LastOrDefault(s => s.Stage == stage);
                            if (existingOutputServer != null)
                            {
                                existingOutputServer.Method = "TCP";
                                existingOutputServer.DataRequest += data;
                                existingOutputServer.ByteSize += byteSize;
                                existingOutputServer.DataTypeMiddleware = dataType;
                            }
                            else
                            {
                                Recorder.OutputServers.Add(new OutputServer
                                {
                                    Stage = stage,
                                    Method = "TCP",
                                    DataRequest = data,
                                    DataTypeMiddleware = dataType,
                                    ByteSize = byteSize
                                });
                            }
                        }
                        else
                        {
                            var existingOutputClient = Recorder.OutputClients.LastOrDefault(c => c.Stage == stage);
                            if (existingOutputClient != null)
                            {
                                existingOutputClient.Method = "TCP";
                                existingOutputClient.DataResponse += data;
                                existingOutputClient.ByteSize += byteSize;
                                existingOutputClient.DataTypeMiddleWare = dataType;

                            }
                            else
                            {
                                Recorder.OutputClients.Add(new OutputClient
                                {
                                    Stage = stage,
                                    Method = "TCP",
                                    DataResponse = data,
                                    DataTypeMiddleWare = dataType,
                                    ByteSize = byteSize
                                });
                            }
                        }
                    });
                }
            }
        }

        private void AddRequestLog(LoggedRequest log) => AppendToFile(log);

        private void AppendToFile(LoggedRequest log)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "middleware_log.txt");
                File.AppendAllText(logPath,
                    $"{DateTime.Now:O} | {log.Method} | {log.Url} | {log.StatusCode}\n{log.RequestBody}\n----\n");
            }
            catch
            {
            }
        }
    }
}

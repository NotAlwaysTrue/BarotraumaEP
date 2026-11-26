using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Barotrauma
{
    // Serves cached performance data and sends management requests to the game thread.
    internal sealed class ServerPerformanceHttpServer : IDisposable
    {
        public const string ContentDirectoryName = "HttpContent";
        private const HttpStatusCode RequestRejected = (HttpStatusCode)418;
        private const string TeapotMessage = "I'm a teapot";
        private const int HistorySeconds = 300;
        private const string RecordingPath = "/api/performance/recording";
        private const string RecordingExportPath = RecordingPath + "/export";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        private sealed record StaticFile(byte[] Bytes, string ContentType);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly bool enabled;
        private readonly Dictionary<string, StaticFile> staticFiles;
        private readonly string panelFlags;
        private readonly HttpListener listener = new HttpListener();
        private readonly byte[] tokenHash;
        private readonly ServerManagementHttp management;
        private readonly ServerPerformanceRecording recording;
        private const int MaxConcurrentRequests = 16;
        private readonly SemaphoreSlim requestSlots = new SemaphoreSlim(MaxConcurrentRequests);
        private readonly ConcurrentDictionary<HttpListenerContext, byte> activeRequests = new ConcurrentDictionary<HttpListenerContext, byte>();
        private readonly ThreadManager.WorkScope requestWork;
        private readonly Queue<ServerPerformanceSnapshot> history = new Queue<ServerPerformanceSnapshot>(HistorySeconds);
        private byte[] snapshot;
        private string serverName;
        private int shutdownRequested;
        private int disposed;

        public Task Completion { get; }

        public ServerPerformanceHttpServer(string prefix, string token, string contentDirectory = null,
            bool tokenEnabled = true, bool enableManagement = false, string recordingDirectory = null,
            string serverName = null, bool enabled = true)
        {
            this.enabled = enabled;
            this.serverName = serverName ?? "";
            panelFlags = $"export const tokenEnabled = {JsonSerializer.Serialize(tokenEnabled)};\n"
                + $"export const enableManagement = {JsonSerializer.Serialize(enableManagement)};\n";
            if (enabled && tokenEnabled)
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length > 1024)
                {
                    throw new ArgumentException("A nonempty token of at most 1024 characters is required.", nameof(token));
                }
                foreach (char character in token)
                {
                    if (character <= ' ' || character > '~')
                    {
                        throw new ArgumentException("The token must contain printable ASCII without spaces.", nameof(token));
                    }
                }
                tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            }
            requestWork = ThreadManager.Current?.CreateScope("Performance HTTP")
                ?? new ThreadManager.WorkScope("Performance HTTP");
            try
            {
                if (enabled)
                {
                    staticFiles = LoadStaticFiles(contentDirectory ?? Path.Combine(AppContext.BaseDirectory, ContentDirectoryName));
                    if (enableManagement) { management = new ServerManagementHttp(); }
                }
                listener.Prefixes.Add(prefix);
                listener.Start();
                if (enabled)
                {
                    recording = new ServerPerformanceRecording(recordingDirectory ??
                        Path.Combine(AppContext.BaseDirectory, ServerPerformanceRecording.DirectoryName), requestWork);
                }
                Completion = requestWork.Schedule("HTTP accept loop", AcceptRequestsAsync);
            }
            catch
            {
                listener.Close();
                requestWork.Dispose();
                throw;
            }
        }

        public void Publish(ServerPerformanceSnapshot value)
        {
            if (!enabled) { return; }
            lock (history)
            {
                while (history.TryPeek(out var oldest) &&
                    (history.Count >= HistorySeconds || value.UptimeSeconds - oldest.UptimeSeconds >= HistorySeconds))
                {
                    history.Dequeue();
                }
                var timings = GetRollingTimings(value.Modules);
                timings.TryGetValue(nameof(ServerPerformanceModule.Update), out var updateTiming);
                var current = value with
                {
                    Spike5mMilliseconds = updateTiming?.SpikeMilliseconds,
                    Mean5mMilliseconds = updateTiming?.MeanMilliseconds,
                    ModuleTimings5m = timings
                };
                history.Enqueue(current);
                Volatile.Write(ref serverName, current.ServerName);
                Volatile.Write(ref snapshot, JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions));
                recording.Record(current);
            }
        }

        private Dictionary<string, ServerRollingModuleTiming> GetRollingTimings(ServerModuleTiming[] current)
        {
            var totals = new Dictionary<string, (double Spike, double Total, long Calls)>();
            foreach (var sample in history)
            {
                Accumulate(sample.Modules);
            }
            Accumulate(current);
            return totals.ToDictionary(pair => pair.Key,
                pair => new ServerRollingModuleTiming(pair.Value.Spike, pair.Value.Total / pair.Value.Calls));

            void Accumulate(ServerModuleTiming[] modules)
            {
                foreach (var timing in modules)
                {
                    if (timing.Samples <= 0) { continue; }
                    totals.TryGetValue(timing.Module, out var total);
                    totals[timing.Module] = (Math.Max(total.Spike, timing.MaxMilliseconds),
                        total.Total + timing.AverageMilliseconds * timing.Samples, total.Calls + timing.Samples);
                }
            }
        }

        private static ServerModuleTiming GetUpdateTiming(ServerPerformanceSnapshot sample) =>
            Array.Find(sample.Modules, timing => timing.Module == nameof(ServerPerformanceModule.Update));

        private byte[] ReadHistory(bool export)
        {
            ServerPerformanceSnapshot[] samples;
            lock (history) { samples = history.ToArray(); }

            if (!export)
            {
                return JsonSerializer.SerializeToUtf8Bytes(samples.Select(sample =>
                {
                    var update = GetUpdateTiming(sample);
                    return new { sample.UptimeSeconds, sample.UpdatesPerSecond, sample.TargetUpdateRate, sample.CpuPercent,
                        sample.WorkingSetBytes, sample.ManagedHeapBytes, AverageUpdateMs = update?.AverageMilliseconds,
                        PeakUpdateMs = update?.MaxMilliseconds, sample.Modules, sample.Spike5mMilliseconds,
                        sample.Mean5mMilliseconds, sample.ModuleTimings5m };
                }), JsonOptions);
            }

            var csv = new StringBuilder();
            ServerPerformanceCsv.AppendHeader(csv);
            foreach (var sample in samples)
            {
                ServerPerformanceCsv.AppendSample(csv, sample);
            }
            return Encoding.UTF8.GetBytes(csv.ToString());
        }

        public void ProcessManagementRequests()
        {
            if (Volatile.Read(ref shutdownRequested) != 0)
            {
                GameMain.ShouldRun = false;
                return;
            }
            management?.ProcessRequests();
        }

        private static Dictionary<string, StaticFile> LoadStaticFiles(string directory)
        {
            string root = Path.GetFullPath(directory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The static content directory must not be a symbolic link.");
            }

            var files = new Dictionary<string, StaticFile>(StringComparer.Ordinal);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                string url = "/" + string.Join("/", Array.ConvertAll(relativePath.Split('/'), Uri.EscapeDataString));
                string contentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".svg" => "image/svg+xml",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".ico" => "image/x-icon",
                    ".woff" => "font/woff",
                    ".woff2" => "font/woff2",
                    ".txt" => "text/plain; charset=utf-8",
                    _ => "application/octet-stream"
                };
                files.Add(url, new StaticFile(File.ReadAllBytes(path), contentType));
            }

            if (!files.TryGetValue("/index.html", out var index))
            {
                throw new FileNotFoundException("The static content directory must contain index.html.");
            }
            files.Add("/", index);
            return files;
        }

        private bool IsAuthorized(string authorization)
        {
            if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || authorization.Length > 1031)
            {
                return false;
            }
            byte[] candidate = SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Substring(7)));
            return CryptographicOperations.FixedTimeEquals(tokenHash, candidate);
        }

        private async Task AcceptRequestsAsync()
        {
            using var stopListener = requestWork.Stopping.Register(listener.Close);
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    if (!requestSlots.Wait(0))
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
                    }
                    activeRequests.TryAdd(context, 0);
                    // Even a cached page or slow response is handled away from the accept loop.
                    try
                    {
                        _ = requestWork.RunBackground("HTTP request", _ => HandleRequestAsync(context));
                    }
                    catch (ObjectDisposedException) when (requestWork.Stopping.IsCancellationRequested)
                    {
                        activeRequests.TryRemove(context, out _);
                        requestSlots.Release();
                        Abort(context.Response);
                        return;
                    }
                }
            }
            catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException || error is InvalidOperationException)
            {
                if (Volatile.Read(ref disposed) == 0 && !requestWork.Stopping.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Performance HTTP listener stopped unexpectedly ({0}).", error.GetType().Name);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestWork.Stopping);
            timeout.CancelAfter(RequestTimeout);
            using var abortOnTimeout = timeout.Token.Register(() => Abort(context.Response));
            bool shutdownAfterReply = false;
            try
            {
                var response = context.Response;
                response.KeepAlive = false;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
                byte[] body;
                string path = context.Request.Url?.AbsolutePath ?? "";
                if (!enabled)
                {
                    body = RejectRequest(response, "panel_disabled");
                }
                else if (path is "/api/management" or "/api/management/logs")
                {
                    (body, shutdownAfterReply) = await HandleManagementAsync(context,
                        path.EndsWith("/logs", StringComparison.Ordinal), timeout.Token).ConfigureAwait(false);
                }
                else if (context.Request.HttpMethod != "GET" &&
                    (path != RecordingPath || context.Request.HttpMethod != "POST"))
                {
                    response.StatusCode = 405;
                    response.Headers["Allow"] = path == RecordingPath ? "GET, POST" : "GET";
                    body = Encoding.UTF8.GetBytes("Method not allowed");
                    response.ContentType = "text/plain; charset=utf-8";
                }
                else if (path is "/api/performance" or "/api/performance/history" or "/api/performance/export"
                    or RecordingPath or RecordingExportPath)
                {
                    response.ContentType = "application/json; charset=utf-8";
                    if (tokenHash != null && !IsAuthorized(context.Request.Headers["Authorization"]))
                    {
                        response.Headers["WWW-Authenticate"] = "Bearer";
                        body = RejectRequest(response, "unauthorized");
                    }
                    else if (path == RecordingPath)
                    {
                        body = await HandleRecordingAsync(context, timeout.Token).ConfigureAwait(false);
                    }
                    else if (path == RecordingExportPath)
                    {
                        using var archive = recording.OpenArchive(out long length);
                        if (archive != null)
                        {
                            await SendRecordingAsync(response, archive, length, timeout).ConfigureAwait(false);
                            return;
                        }
                        response.StatusCode = 404;
                        body = Encoding.UTF8.GetBytes("{\"error\":\"no_saved_recording\"}");
                    }
                    else if (path != "/api/performance")
                    {
                        bool export = path == "/api/performance/export";
                        if (export)
                        {
                            response.ContentType = "text/csv; charset=utf-8";
                            response.Headers["Content-Disposition"] = "attachment; filename=\"performance.csv\"";
                        }
                        body = ReadHistory(export);
                    }
                    else
                    {
                        response.Headers["X-Barotrauma-Recording"] = JsonSerializer.Serialize(recording.GetState(), JsonOptions);
                        body = Volatile.Read(ref snapshot);
                        if (body == null)
                        {
                            response.StatusCode = 503;
                            body = Encoding.UTF8.GetBytes("{\"error\":\"waiting_for_sample\"}");
                        }
                    }
                }
                else if (path == "/panel-config.js")
                {
                    response.ContentType = "text/javascript; charset=utf-8";
                    body = Encoding.UTF8.GetBytes(panelFlags
                        + $"export const serverName = {JsonSerializer.Serialize(Volatile.Read(ref serverName))};\n");
                }
                else if (staticFiles.TryGetValue(path, out var file))
                {
                    body = file.Bytes;
                    response.ContentType = file.ContentType;
                }
                else
                {
                    response.StatusCode = 404;
                    body = Encoding.UTF8.GetBytes("Not found");
                    response.ContentType = "text/plain; charset=utf-8";
                }
                if (response.StatusCode == (int)RequestRejected)
                {
                    response.StatusDescription = TeapotMessage;
                }
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
                response.Close();
            }
            catch (Exception error) when (error is HttpListenerException || error is IOException
                || error is ObjectDisposedException || error is OperationCanceledException || error is InvalidOperationException)
            {
                // Disconnected, slow or aborted clients must not affect the game loop.
                Abort(context.Response);
            }
            finally
            {
                activeRequests.TryRemove(context, out _);
                requestSlots.Release();
                if (shutdownAfterReply)
                {
                    // Let the game loop finish and flush performance recordings after acknowledging the request.
                    Volatile.Write(ref shutdownRequested, 1);
                }
            }
        }

        private static byte[] RejectRequest(HttpListenerResponse response, string error)
        {
            response.StatusCode = (int)RequestRejected;
            response.ContentType = "application/json; charset=utf-8";
            return JsonSerializer.SerializeToUtf8Bytes(new { Error = error, Message = TeapotMessage }, JsonOptions);
        }

        private async Task<byte[]> HandleRecordingAsync(HttpListenerContext context, CancellationToken cancellation)
        {
            var request = context.Request;
            var response = context.Response;
            if (request.HttpMethod == "GET")
            {
                return JsonSerializer.SerializeToUtf8Bytes(recording.GetState(), JsonOptions);
            }
            if (request.Headers["X-Barotrauma-Panel"] != "1")
            {
                return RejectRequest(response, "missing_panel_header");
            }
            string mediaType = request.ContentType?.Split(';', 2)[0].Trim();
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 415;
                return Encoding.UTF8.GetBytes("{\"error\":\"expected_json\"}");
            }
            if (request.ContentLength64 > 16)
            {
                response.StatusCode = 413;
                return Encoding.UTF8.GetBytes("{\"error\":\"request_too_large\"}");
            }
            if (request.ContentLength64 > 0)
            {
                var bytes = new byte[(int)request.ContentLength64];
                try
                {
                    await request.InputStream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                    bool enabled = JsonSerializer.Deserialize<bool>(bytes);
                    return JsonSerializer.SerializeToUtf8Bytes(recording.SetEnabled(enabled), JsonOptions);
                }
                catch (JsonException)
                {
                    // Fall through to the invalid request response.
                }
            }
            response.StatusCode = 400;
            return Encoding.UTF8.GetBytes("{\"error\":\"expected_boolean\"}");
        }

        private static async Task SendRecordingAsync(HttpListenerResponse response, FileStream archive, long length,
            CancellationTokenSource timeout)
        {
            response.ContentType = "text/csv; charset=utf-8";
            response.Headers["Content-Disposition"] = "attachment; filename=\"performance-recording.csv\"";
            response.ContentLength64 = length;
            var buffer = new byte[65536];
            while (length > 0)
            {
                int read = await archive.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), timeout.Token).ConfigureAwait(false);
                if (read == 0) { throw new EndOfStreamException(); }
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                length -= read;
                timeout.CancelAfter(RequestTimeout);
            }
            response.Close();
        }

        private async Task<(byte[] Body, bool ShutdownAfterReply)> HandleManagementAsync(
            HttpListenerContext context, bool readLogs, CancellationToken cancellation)
        {
            var request = context.Request;
            var response = context.Response;
            response.ContentType = "application/json; charset=utf-8";
            if (management == null)
            {
                return ManagementError(RequestRejected, "Server management is unavailable.");
            }
            if (tokenHash != null && !IsAuthorized(request.Headers["Authorization"]))
            {
                response.Headers["WWW-Authenticate"] = "Bearer";
                return ManagementError(RequestRejected, "The access token is invalid.");
            }
            if (readLogs)
            {
                if (request.HttpMethod != "GET")
                {
                    response.Headers["Allow"] = "GET";
                    return ManagementError(HttpStatusCode.MethodNotAllowed, "Method not allowed.");
                }
                var logs = await ReadLogsAsync(request.QueryString["file"], cancellation).ConfigureAwait(false);
                response.StatusCode = (int)logs.Status;
                return (JsonSerializer.SerializeToUtf8Bytes(logs.Body, JsonOptions), false);
            }
            if (request.HttpMethod != "GET" && request.HttpMethod != "POST")
            {
                response.Headers["Allow"] = "GET, POST";
                return ManagementError(HttpStatusCode.MethodNotAllowed, "Method not allowed.");
            }

            ServerManagementHttp.Command command = null;
            if (request.HttpMethod == "POST")
            {
                // Cross-origin browser requests require a preflight for this header, which the listener does not allow.
                if (request.Headers["X-Barotrauma-Panel"] != "1")
                {
                    return ManagementError(RequestRejected, "The management request header is missing.");
                }
                string mediaType = request.ContentType?.Split(';', 2)[0].Trim();
                if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return ManagementError(HttpStatusCode.UnsupportedMediaType, "The request must contain JSON.");
                }
                if (request.ContentLength64 <= 0)
                {
                    return ManagementError(HttpStatusCode.BadRequest, "A JSON request body is required.");
                }
                if (request.ContentLength64 > 16384)
                {
                    return ManagementError(HttpStatusCode.RequestEntityTooLarge, "The management request is too large.");
                }
                var bytes = new byte[(int)request.ContentLength64];
                try
                {
                    await request.InputStream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
                    command = JsonSerializer.Deserialize<ServerManagementHttp.Command>(bytes, JsonOptions);
                }
                catch (JsonException)
                {
                    return ManagementError(HttpStatusCode.BadRequest, "Invalid management request JSON.");
                }
                if (string.IsNullOrEmpty(command?.Action))
                {
                    return ManagementError(HttpStatusCode.BadRequest, "A management action is required.");
                }
            }

            var reply = await management.RequestAsync(command, cancellation).ConfigureAwait(false);
            response.StatusCode = (int)reply.Status;
            return (JsonSerializer.SerializeToUtf8Bytes(reply.Body, JsonOptions), reply.ShutdownAfterReply);

            (byte[] Body, bool ShutdownAfterReply) ManagementError(HttpStatusCode status, string message)
            {
                response.StatusCode = (int)status;
                byte[] body = status == RequestRejected
                    ? JsonSerializer.SerializeToUtf8Bytes(new { ErrorMessage = message, Message = TeapotMessage }, JsonOptions)
                    : JsonSerializer.SerializeToUtf8Bytes(new { ErrorMessage = message }, JsonOptions);
                return (body, false);
            }
        }

        private async Task<ServerManagementHttp.Reply> ReadLogsAsync(string fileName, CancellationToken cancellation)
        {
            var root = new DirectoryInfo(Barotrauma.Networking.ServerLog.SavePath);
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
            FileInfo[] files = root.Exists && (root.Attributes & FileAttributes.ReparsePoint) == 0
                ? root.EnumerateFiles("*.txt", options).OrderByDescending(file => file.LastWriteTimeUtc).Take(100).ToArray()
                : Array.Empty<FileInfo>();
            if (string.IsNullOrEmpty(fileName))
            {
                var live = await management.RequestAsync(null, cancellation, readLogs: true).ConfigureAwait(false);
                if (live.Status != HttpStatusCode.OK) { return live; }
                return new ServerManagementHttp.Reply(HttpStatusCode.OK, new { Files = files.Select(file => file.Name), Live = live.Body });
            }

            var match = Array.Find(files, file => file.Name == fileName);
            if (match == null)
            {
                return new ServerManagementHttp.Reply(HttpStatusCode.NotFound, new { ErrorMessage = "The log file is unavailable." });
            }
            using var stream = match.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxBytes = 256 * 1024;
            bool truncated = stream.Length > maxBytes;
            if (truncated) { stream.Seek(-maxBytes, SeekOrigin.End); }
            using var reader = new StreamReader(stream);
            var buffer = new char[maxBytes];
            int length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
            int start = truncated ? Array.IndexOf(buffer, '\n', 0, length) + 1 : 0;
            return new ServerManagementHttp.Reply(HttpStatusCode.OK,
                new { Contents = new string(buffer, start, length - start), Truncated = truncated });
        }

        private static void Abort(HttpListenerResponse response)
        {
            try
            {
                response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            requestWork.RequestStop();
            foreach (var context in activeRequests.Keys)
            {
                Abort(context.Response);
            }
            requestWork.Dispose();
            listener.Close();
            requestSlots.Dispose();
        }
    }
}

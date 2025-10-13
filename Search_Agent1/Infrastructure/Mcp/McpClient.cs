using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;              // <-- ADDED
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Mcp
{
    public sealed class McpClient : IDisposable
    {
        private readonly Process _proc;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private readonly TimeSpan _requestTimeout;
        private readonly int _maxRetries;
        private readonly object _lock = new();

        private readonly StringBuilder _stderr = new();   // <-- ADDED

        public McpClient(string command, IEnumerable<string> args, IDictionary<string, string>? env, TimeSpan requestTimeout, int maxRetries, int readyTimeoutMs = 8000)
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (env != null)
                foreach (var kv in env) _proc.StartInfo.Environment[kv.Key] = kv.Value;

            if (!_proc.Start())
                throw new InvalidOperationException("Failed to start MCP tool process.");

            // ==== ADDED: begin capturing STDERR from the Node server ====
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e?.Data != null) _stderr.AppendLine(e.Data);
            };
            _proc.BeginErrorReadLine();
            // ============================================================

            _stdin = _proc.StandardInput;
            _stdout = _proc.StandardOutput;
            _requestTimeout = requestTimeout;
            _maxRetries = Math.Max(0, maxRetries);

            // Optional: wait briefly for server to be ready
            Thread.Sleep(readyTimeoutMs);
        }

        private async Task<T> Send<T>(string method, object? @params, CancellationToken ct)
        {
            var req = new JsonRpcRequest { Method = method, Params = @params };
            var json = JsonSerializer.Serialize(req);
            lock (_lock)
            {
                _stdin.WriteLine(json);
                _stdin.Flush();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_requestTimeout);

            while (!cts.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync();
                if (line is null)
                    throw new IOException("MCP server closed the stream. STDERR:\n" + _stderr.ToString());  // <-- CHANGED

                try
                {
                    var resp = JsonSerializer.Deserialize<JsonRpcResponse<T>>(line);
                    if (resp != null && resp.Id == req.Id)
                    {
                        if (resp.Error != null) throw new InvalidOperationException(resp.Error.Message);
                        if (resp.Result == null) throw new InvalidOperationException("Empty result.");
                        return resp.Result;
                    }
                }
                catch { /* keep reading until matching id */ }
            }

            throw new TimeoutException($"MCP request timeout for method {method}");
        }

        private async Task<T> WithRetry<T>(Func<CancellationToken, Task<T>> call, int retries)
        {
            var attempt = 0;
            while (true)
            {
                try { return await call(CancellationToken.None); }
                catch when (attempt++ < retries) { await Task.Delay(200 * attempt); }
            }
        }

        public Task<ToolsListResult> ListToolsAsync() =>
            WithRetry(ct => Send<ToolsListResult>("tools/list", new { }, ct), _maxRetries);

        public Task<ToolCallEnvelope> CallToolAsync(string name, object args) =>
            WithRetry(ct => Send<ToolCallEnvelope>("tools/call", new ToolCallParams { Name = name, Arguments = args }, ct), _maxRetries);

        public void Dispose()
        {
            try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
            _proc.Dispose();
        }
    }
}

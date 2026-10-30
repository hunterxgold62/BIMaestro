using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BIMaestro.Codex
{
    // Minimal MCP server (Streamable HTTP, JSON responses) exposing the Revit tools to one
    // Claude Code process. Loopback only, protected by a random bearer token, and it lives
    // for a single request. Tool calls run on the SynchronizationContext that called Start
    // (the panel's UI thread), exactly like the previous one-tool-per-launch transport.
    internal sealed class ClaudeToolServer : IDisposable
    {
        internal const string ServerName = "bimaestro";
        private const int MaxBodyBytes = 64 * 1024 * 1024;

        private readonly TcpListener listener;
        private readonly JArray tools;
        private readonly HashSet<string> toolNames;
        private readonly Func<string, JObject, Task<string>> toolCall;
        private readonly SynchronizationContext context;
        private readonly SemaphoreSlim revitGate = new SemaphoreSlim(1, 1);
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private volatile bool disposed;

        internal string Url { get; }
        internal string Token { get; }
        internal int ToolCallCount;

        private ClaudeToolServer(JArray definitions, Func<string, JObject, Task<string>> toolCall)
        {
            tools = new JArray(definitions.OfType<JObject>().Select(d => new JObject
            {
                ["name"] = d["name"], ["description"] = d["description"],
                ["inputSchema"] = d["inputSchema"] ?? new JObject { ["type"] = "object" }
            }));
            toolNames = new HashSet<string>(tools.Select(t => (string)t["name"]), StringComparer.Ordinal);
            this.toolCall = toolCall;
            context = SynchronizationContext.Current;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/mcp";
        }

        internal static ClaudeToolServer Start(JArray definitions, Func<string, JObject, Task<string>> toolCall)
        {
            var server = new ClaudeToolServer(definitions, toolCall);
            _ = server.AcceptLoopAsync();
            return server;
        }

        // Content of the --mcp-config file. Contains the token: keep it in the user's
        // local storage and delete it after the request.
        internal string McpConfigJson() => new JObject
        {
            ["mcpServers"] = new JObject
            {
                [ServerName] = new JObject
                {
                    ["type"] = "http", ["url"] = Url,
                    ["headers"] = new JObject { ["Authorization"] = "Bearer " + Token }
                }
            }
        }.ToString(Formatting.None);

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) when (disposed) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (disposed) return; continue; }
                lock (clients) clients.Add(client);
                _ = ServeConnectionAsync(client);
            }
        }

        private async Task ServeConnectionAsync(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                while (!disposed)
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request == null) return;
                    if (await TryStreamToolCallAsync(stream, request).ConfigureAwait(false))
                    {
                        if (request.Close) return;
                        continue;
                    }
                    var response = await HandleAsync(request).ConfigureAwait(false);
                    await WriteResponseAsync(stream, response.status, response.body).ConfigureAwait(false);
                    if (request.Close) return;
                }
            }
            catch (Exception) { /* Connection closed by Claude Code or by Dispose. */ }
            finally
            {
                lock (clients) clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }

        private sealed class HttpRequest
        {
            internal string Method, Path, Authorization, Body;
            internal bool Close, AcceptsEventStream;
        }

        private static async Task<HttpRequest> ReadRequestAsync(Stream stream)
        {
            var header = new MemoryStream();
            var one = new byte[1];
            int matched = 0;
            while (matched < 4)
            {
                int read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (read == 0) return null;
                header.WriteByte(one[0]);
                matched = (one[0] == (matched % 2 == 0 ? (byte)'\r' : (byte)'\n')) ? matched + 1 : (one[0] == '\r' ? 1 : 0);
                if (header.Length > 64 * 1024) throw new InvalidDataException("En-têtes trop longs.");
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) throw new InvalidDataException("Requête invalide.");
            var request = new HttpRequest { Method = first[0], Path = first[1].Split('?')[0] };
            int length = 0; bool chunked = false;
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim(), value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) request.Authorization = value;
                else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) request.Close = value.Equals("close", StringComparison.OrdinalIgnoreCase);
                else if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase)) request.AcceptsEventStream = value.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            byte[] body = chunked ? await ReadChunkedAsync(stream).ConfigureAwait(false) : await ReadExactAsync(stream, length).ConfigureAwait(false);
            request.Body = Encoding.UTF8.GetString(body);
            return request;
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
        {
            if (length < 0 || length > MaxBodyBytes) throw new InvalidDataException("Requête trop volumineuse.");
            var buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer, offset, length - offset).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private static async Task<string> ReadLineAsync(Stream stream)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) throw new EndOfStreamException();
                if (one[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)one[0]);
            }
        }

        private static async Task<byte[]> ReadChunkedAsync(Stream stream)
        {
            var body = new MemoryStream();
            while (true)
            {
                string size = (await ReadLineAsync(stream).ConfigureAwait(false)).Split(';')[0].Trim();
                int length = Convert.ToInt32(size, 16);
                if (length == 0) { while ((await ReadLineAsync(stream).ConfigureAwait(false)).Length > 0) { } return body.ToArray(); }
                if (body.Length + length > MaxBodyBytes) throw new InvalidDataException("Requête trop volumineuse.");
                var chunk = await ReadExactAsync(stream, length).ConfigureAwait(false);
                body.Write(chunk, 0, chunk.Length);
                await ReadLineAsync(stream).ConfigureAwait(false);
            }
        }

        private static async Task WriteResponseAsync(Stream stream, int status, string body)
        {
            byte[] content = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            string reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Bad Request";
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                (body != null ? "Content-Type: application/json; charset=utf-8\r\n" : "") +
                "Content-Length: " + content.Length + "\r\nCache-Control: no-store\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            if (content.Length > 0) await stream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        // A Revit operation can take minutes. For a single tools/call the answer is sent as
        // an SSE stream: headers at once, a comment every 20 s, then the result. The HTTP
        // client never sits idle long enough to drop the request.
        private async Task<bool> TryStreamToolCallAsync(Stream stream, HttpRequest request)
        {
            if (request.Method != "POST" || request.Path != "/mcp" || !request.AcceptsEventStream ||
                !string.Equals(request.Authorization, "Bearer " + Token, StringComparison.Ordinal)) return false;
            JObject message;
            try { message = JToken.Parse(request.Body) as JObject; }
            catch (JsonException) { return false; }
            if (message == null || (string)message["method"] != "tools/call" || message["id"] == null) return false;

            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nTransfer-Encoding: chunked\r\n\r\n");
            var writeLock = new SemaphoreSlim(1, 1);
            async Task Chunk(string text)
            {
                var data = Encoding.UTF8.GetBytes(text);
                var prefix = Encoding.ASCII.GetBytes(data.Length.ToString("X") + "\r\n");
                var suffix = Encoding.ASCII.GetBytes("\r\n");
                await writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(prefix, 0, prefix.Length).ConfigureAwait(false);
                    await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                    await stream.WriteAsync(suffix, 0, suffix.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                finally { writeLock.Release(); }
            }
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            await Chunk(": bimaestro\n\n").ConfigureAwait(false);
            var work = HandleMessageAsync(message);
            while (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false) != work)
                await Chunk(": revit en cours\n\n").ConfigureAwait(false);
            var answer = await work.ConfigureAwait(false);
            await Chunk("event: message\ndata: " + answer.ToString(Formatting.None) + "\n\n").ConfigureAwait(false);
            var end = Encoding.ASCII.GetBytes("0\r\n\r\n");
            await stream.WriteAsync(end, 0, end.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return true;
        }

        private async Task<(int status, string body)> HandleAsync(HttpRequest request)
        {
            if (!string.Equals(request.Authorization, "Bearer " + Token, StringComparison.Ordinal)) return (401, null);
            if (request.Path != "/mcp") return (404, null);
            if (request.Method == "DELETE") return (200, null);
            if (request.Method != "POST") return (405, null);
            JToken message;
            try { message = JToken.Parse(request.Body); }
            catch (JsonException) { return (400, Error(null, -32700, "JSON invalide.").ToString(Formatting.None)); }
            if (message is JArray batch)
            {
                var answers = new JArray();
                foreach (var item in batch.OfType<JObject>())
                {
                    var answer = await HandleMessageAsync(item).ConfigureAwait(false);
                    if (answer != null) answers.Add(answer);
                }
                return answers.Count == 0 ? (202, null) : (200, answers.ToString(Formatting.None));
            }
            var single = message is JObject obj ? await HandleMessageAsync(obj).ConfigureAwait(false) : Error(null, -32600, "Requête invalide.");
            return single == null ? (202, null) : (200, single.ToString(Formatting.None));
        }

        private async Task<JObject> HandleMessageAsync(JObject message)
        {
            var id = message["id"];
            string method = (string)message["method"];
            if (id == null || method == null) return null; // Notification or client response.
            switch (method)
            {
                case "initialize":
                    return Result(id, new JObject
                    {
                        ["protocolVersion"] = (string)message["params"]?["protocolVersion"] ?? "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = "1.0.0" }
                    });
                case "ping":
                    return Result(id, new JObject());
                case "tools/list":
                    return Result(id, new JObject { ["tools"] = tools });
                case "tools/call":
                    string name = (string)message["params"]?["name"];
                    var arguments = message["params"]?["arguments"] as JObject ?? new JObject();
                    if (name == null || !toolNames.Contains(name))
                        return Result(id, ToolResult("Outil Revit inconnu : " + name, true));
                    string text = await CallOnContextAsync(name, arguments).ConfigureAwait(false);
                    return Result(id, ToolResult(text, false));
                default:
                    return Error(id, -32601, "Méthode non prise en charge : " + method);
            }
        }

        // Revit tools run one at a time, on the panel's context, as before.
        private async Task<string> CallOnContextAsync(string name, JObject arguments)
        {
            await revitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref ToolCallCount);
                if (context == null) return await toolCall(name, arguments).ConfigureAwait(false);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                context.Post(async _ =>
                {
                    try { completion.TrySetResult(await toolCall(name, arguments)); }
                    catch (Exception ex) { completion.TrySetResult("Erreur BIMaestro pendant " + name + " : " + ex.Message); }
                }, null);
                return await completion.Task.ConfigureAwait(false);
            }
            finally { revitGate.Release(); }
        }

        private static JObject ToolResult(string text, bool isError) => new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text ?? "" }),
            ["isError"] = isError
        };

        private static JObject Result(JToken id, JObject result) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JObject Error(JToken id, int code, string text) => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = text }
        };

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { listener.Stop(); } catch { }
            lock (clients) { foreach (var client in clients) try { client.Close(); } catch { } clients.Clear(); }
        }
    }
}

// Runs the official Claude CLI against a loopback-only model API fixture. No real login or model inference.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace BIMaestro.Codex
{
    internal class CodexImageAttachment { internal string DataUrl; }
    internal static class CodexRevitBridge { internal static JArray ToolDefinitions(bool mepMode = false) => new JArray(); }
    internal static class CodexWindow { internal const string MepDeveloperInstructions = "Test"; }
    internal static class ClaudeNativeTransportProbe
    {
        static int messages, images;
        static bool rejectImages;
        static bool unicodeSeen, imageBytesValid = true;
        static string expectedPng;
        static async Task<int> Main(string[] args)
        {
            string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) { string k = (string)e.Key; if (k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable(k, null); }
            var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
            string endpoint = "http://127.0.0.1:" + port + "/";
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", endpoint.TrimEnd('/'));
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "transport-test-not-a-real-token");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1");
            using (var server = new HttpListener())
            {
                server.Prefixes.Add(endpoint); server.Start();
                var serve = Task.Run(async () => { try { while (server.IsListening) { var ctx = await server.GetContextAsync(); await Respond(ctx); } } catch (HttpListenerException) { } catch (ObjectDisposedException) { } });
                try
                {
                    using (var client = new ClaudeClient(Path.GetFullPath(args[0]), root)) using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                    {
                        string reply = null;
                        string png = "data:image/png;base64," + Convert.ToBase64String(CreatePng());
                        expectedPng = png.Substring("data:image/png;base64,".Length);
                        var pages = new[] { new CodexImageAttachment { DataUrl = png }, new CodexImageAttachment { DataUrl = png }, new CodexImageAttachment { DataUrl = png } };
                        await client.AskAsync("Fiche PDF : été 600 × 400 mm. " + new string('x', 8191) + "😀" + new string('y', 21000), pages, "sonnet", "high", (_, __) => throw new Exception("Unexpected Revit call"), s => reply = s, cancel.Token);
                        if (reply != "Vision transport OK" || images != 3 || !unicodeSeen || !imageBytesValid) throw new Exception("Image payload, UTF-8 or structured output was lost. images=" + images + " unicode=" + unicodeSeen + " exactImages=" + imageBytesValid + " reply=" + reply);
                        string session = client.SessionId;
                        await client.AskAsync("Continue la fiche", Array.Empty<CodexImageAttachment>(), "sonnet", "high", (_, __) => throw new Exception("Unexpected Revit call"), s => reply = s, cancel.Token);
                        if (session != client.SessionId || reply != "Vision transport OK") throw new Exception("Native session resume failed.");
                        Console.WriteLine("PASS Runtime=" + Environment.Version + " real CLI, 3 PNG pages, " + (png.Length * 3) + " base64 characters, UTF-8, structured output, native resume");
                        rejectImages = true; client.NewDiscussion();
                        bool rejected = false;
                        try { await client.AskAsync("Rejected PDF fixture", pages, "sonnet", "high", (_, __) => throw new Exception("Unexpected Revit call"), _ => { }, cancel.Token); }
                        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("Synthetic rejected image") && ex.Message.Length < 2600; }
                        if (!rejected) throw new Exception("The API image rejection diagnostic was lost.");
                        string diagnostic = File.ReadAllText(client.LastDiagnosticPath);
                        if (diagnostic.Contains("Fiche PDF") || diagnostic.Contains("base64") || diagnostic.Contains("transport-test-not-a-real-token")) throw new Exception("Sensitive payload in transport diagnostic.");
                        Console.WriteLine("PASS real CLI/API error surfaced without protocol dump; diagnostic excludes payload and credentials");
                    }
                }
                catch (Exception ex) { Console.WriteLine(ex); return 1; }
                finally { server.Stop(); await serve; }
            }
            return 0;
        }
        static async Task Respond(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod == "HEAD") { ctx.Response.Close(); return; }
            string body; using (var read = new StreamReader(ctx.Request.InputStream)) body = await read.ReadToEndAsync();
            var request = string.IsNullOrEmpty(body) ? new JObject() : JObject.Parse(body);
            if (ctx.Request.Url.AbsolutePath != "/v1/messages") { byte[] trivial = Encoding.UTF8.GetBytes("{\"input_tokens\":100}"); ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = trivial.Length; await ctx.Response.OutputStream.WriteAsync(trivial, 0, trivial.Length); ctx.Response.Close(); return; }
            messages++;
            if (rejectImages) { ctx.Response.StatusCode = 400; byte[] failure = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Synthetic rejected image\"}}"); ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = failure.Length; await ctx.Response.OutputStream.WriteAsync(failure, 0, failure.Length); ctx.Response.Close(); return; }
            foreach (var msg in request["messages"] ?? new JArray()) if (msg["content"] is JArray blocks) foreach (var block in blocks)
                    {
                        if ((string)block["type"] == "image") { images++; imageBytesValid &= ValidImage(block["source"]); }
                        if ((string)block["type"] == "text") { string text = (string)block["text"] ?? ""; unicodeSeen |= text.Contains("été 600 × 400") && text.Contains("😀") && !text.Contains("\uFFFD"); }
                    }
            bool resultAlreadySent = false;
            var content = resultAlreadySent ? new JObject { ["type"] = "text", ["text"] = "Complete" } : new JObject { ["type"] = "tool_use", ["id"] = "toolu_transport", ["name"] = "StructuredOutput", ["input"] = new JObject { ["reply"] = "Vision transport OK", ["tool"] = "", ["arguments"] = new JObject(), ["done"] = true } };
            var message = new JObject { ["id"] = "msg_transport", ["type"] = "message", ["role"] = "assistant", ["model"] = request["model"], ["content"] = new JArray(content), ["stop_reason"] = resultAlreadySent ? "end_turn" : "tool_use", ["stop_sequence"] = null, ["usage"] = new JObject { ["input_tokens"] = 100, ["output_tokens"] = 30 } };
            if (request.Value<bool?>("stream") == true)
            {
                var start = (JObject)message.DeepClone(); start["content"] = new JArray(); start["stop_reason"] = null;
                var blockStart = (JObject)content.DeepClone(); if (resultAlreadySent) blockStart["text"] = ""; else blockStart["input"] = new JObject();
                string s = Event("message_start", new JObject { ["message"] = start }) + Event("content_block_start", new JObject { ["index"] = 0, ["content_block"] = blockStart }) +
                Event("content_block_delta", new JObject { ["index"] = 0, ["delta"] = resultAlreadySent ? new JObject { ["type"] = "text_delta", ["text"] = "Complete" } : new JObject { ["type"] = "input_json_delta", ["partial_json"] = content["input"].ToString(Newtonsoft.Json.Formatting.None) } }) +
                Event("content_block_stop", new JObject { ["index"] = 0 }) + Event("message_delta", new JObject { ["delta"] = new JObject { ["stop_reason"] = message["stop_reason"], ["stop_sequence"] = null }, ["usage"] = new JObject { ["output_tokens"] = 30 } }) + Event("message_stop", new JObject());
                byte[] data = Encoding.UTF8.GetBytes(s); ctx.Response.ContentType = "text/event-stream"; ctx.Response.ContentLength64 = data.Length; await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length);
            }
            else { byte[] data = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None)); ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = data.Length; await ctx.Response.OutputStream.WriteAsync(data, 0, data.Length); }
            ctx.Response.Close();
        }
        static string Event(string type, JObject data) { data["type"] = type; return "event: " + type + "\ndata: " + data.ToString(Newtonsoft.Json.Formatting.None) + "\n\n"; }
        static bool ValidImage(JToken source)
        {
            string media = (string)source?["media_type"], data = (string)source?["data"];
            if (media == "image/png") return data == expectedPng;
            // Claude Code recompresses large PNG inputs to JPEG before its API request.
            if (media != "image/jpeg" || string.IsNullOrEmpty(data)) return false;
            byte[] jpeg = Convert.FromBase64String(data);
            if (jpeg.Length < 1000 || jpeg[0] != 255 || jpeg[1] != 216) return false;
            for (int i = 2; i + 8 < jpeg.Length;)
            {
                if (jpeg[i++] != 255) return false; while (i < jpeg.Length && jpeg[i] == 255) i++;
                int marker = jpeg[i++]; if (marker == 216) continue; if (marker == 217 || marker == 218) return false;
                int length = (jpeg[i] << 8) | jpeg[i + 1]; if (length < 2 || i + length > jpeg.Length) return false;
                if (marker == 192 || marker == 194) return ((jpeg[i + 3] << 8) | jpeg[i + 4]) == 650 && ((jpeg[i + 5] << 8) | jpeg[i + 6]) == 650;
                i += length;
            }
            return false;
        }
        // Valid 650x650 RGB PNG with uncompressed deflate blocks; no imaging/runtime dependency.
        static byte[] CreatePng()
        {
            const int width = 650; byte[] raw = new byte[width * (width * 3 + 1)]; new Random(42).NextBytes(raw);
            for (int y = 0; y < width; y++) raw[y * (width * 3 + 1)] = 0;
            using (var png = new MemoryStream()) using (var z = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                byte[] header = new byte[13]; Array.Copy(Big(width), 0, header, 0, 4); Array.Copy(Big(width), 0, header, 4, 4); header[8] = 8; header[9] = 2; Chunk(png, "IHDR", header);
                z.WriteByte(0x78); z.WriteByte(1);
                for (int pos = 0; pos < raw.Length;) { int count = Math.Min(65535, raw.Length - pos); z.WriteByte((byte)(pos + count == raw.Length ? 1 : 0)); z.WriteByte((byte)count); z.WriteByte((byte)(count >> 8)); z.WriteByte((byte)~count); z.WriteByte((byte)(~count >> 8)); z.Write(raw, pos, count); pos += count; }
                uint a = 1, b = 0; foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
                byte[] adler = Big(unchecked((int)((b << 16) | a))); z.Write(adler, 0, 4);
                Chunk(png, "IDAT", z.ToArray()); Chunk(png, "IEND", new byte[0]); return png.ToArray();
            }
        }
        static byte[] Big(int n) => new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        static void Chunk(Stream output, string name, byte[] data)
        {
            byte[] length = Big(data.Length), tag = Encoding.ASCII.GetBytes(name); output.Write(length, 0, 4); output.Write(tag, 0, 4); output.Write(data, 0, data.Length);
            uint crc = 0xffffffff; foreach (byte v in tag) { crc ^= v; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
            foreach (byte v in data) { crc ^= v; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
            byte[] checksum = Big(unchecked((int)~crc)); output.Write(checksum, 0, 4);
        }
    }
}

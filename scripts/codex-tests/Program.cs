using BIMaestro.Codex;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "app-server") return FakeServer();
        string root = Path.GetFullPath(Path.Combine("tmp", "codex-tests", Guid.NewGuid().ToString("N")));
        try
        {
            FamilyDesignTests.Run();
            ParametricDesignTests.Run(); FamilyParameterTests.Run();
            var shapes = JObject.Parse("{\"boxes\":[{\"x_mm\":0,\"y_mm\":0,\"z_mm\":0,\"height_mm\":100,\"width_mm\":200,\"length_mm\":300}],\"cylinders\":[{\"x_mm\":10,\"y_mm\":20,\"z_mm\":30,\"height_mm\":400,\"radius_mm\":50}]}");
            var batch = CodexShapeBatch.Parse(shapes);
            Check(batch.Count == 2 && !batch[0].IsCylinder && batch[1].IsCylinder && batch[1].Radius == 50, "mixed shape batch");
            foreach (var invalid in new[]
            {
                JObject.Parse("{\"boxes\":[],\"cylinders\":[]}"),
                new JObject { ["boxes"] = new JArray(System.Linq.Enumerable.Repeat(shapes["boxes"][0], 51)), ["cylinders"] = new JArray() },
                JObject.Parse("{\"boxes\":[],\"cylinders\":[{\"x_mm\":0,\"y_mm\":0,\"z_mm\":0,\"height_mm\":1,\"radius_mm\":0}]}"),
                JObject.Parse("{\"boxes\":[],\"cylinders\":[{\"x_mm\":0,\"y_mm\":0,\"z_mm\":0,\"height_mm\":1,\"radius_mm\":\"50\"}]}"),
                JObject.Parse("{\"boxes\":[{}],\"cylinders\":[]}")
            })
            {
                bool rejected = false;
                try { CodexShapeBatch.Parse(invalid); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "invalid batch rejected before transaction");
            }
            using (var client = new CodexClient(root))
            {
                var notification = new TaskCompletionSource<bool>();
                client.Notification += (method, data) => { if (method == "test/notification") notification.TrySetResult(true); };
                client.ServerRequest = (method, data) => Task.FromResult<object>(new { accepted = method == "test/request" });
                await client.StartAsync(Assembly.GetExecutingAssembly().Location);
                var unicode = await client.RequestAsync("echo", new { text = "Créer une boîte : 600 × 400 mm — élévation" });
                Check((string)unicode["text"] == "Créer une boîte : 600 × 400 mm — élévation", "UTF-8 roundtrip");
                var first = client.RequestAsync("first", new { });
                var second = client.RequestAsync("second", new { });
                await Task.WhenAll(first, second);
                Check((string)first.Result["value"] == "first" && (string)second.Result["value"] == "second", "out-of-order correlation");
                bool error = false;
                try { await client.RequestAsync("fail", new { }); } catch (InvalidOperationException) { error = true; }
                Check(error, "RPC errors surfaced");
                await client.RequestAsync("notify", new { });
                Check(await Task.WhenAny(notification.Task, Task.Delay(2000)) == notification.Task, "notifications");
                var callback = await client.RequestAsync("callback", new { });
                Check(callback.Value<bool>("accepted"), "server request roundtrip");
                var hang = client.RequestAsync("hang", new { });
                client.Dispose();
                bool disconnected = false;
                try { await hang; } catch (IOException) { disconnected = true; }
                Check(disconnected, "pending request cancelled on disposal");
            }
            if (args.Length == 1)
            {
                using (var client = new CodexClient(Path.Combine(root, "real")))
                {
                    await client.StartAsync(args[0]);
                    var config = await client.RequestAsync("config/read", new { includeLayers = false });
                    Check((string)config["config"]?["forced_login_method"] == "chatgpt", "real Codex: ChatGPT-only login");
                    Check((string)config["config"]?["cli_auth_credentials_store"] == "keyring", "real Codex: OS credential store");
                    Check(config["config"]?["features"]?.Value<bool?>("shell_tool") == false, "real Codex: shell disabled");
                    Check((string)config["config"]?["sandbox_mode"] == "read-only", "real Codex: read-only sandbox");
                    var thread = await client.RequestAsync("thread/start", new
                    {
                        ephemeral = true, sandbox = "read-only", approvalPolicy = "on-request", approvalsReviewer = "user",
                        environments = new object[0],
                        dynamicTools = new JArray(CodexFamilyDesign.Tool(), CodexFamilyDesign.Tool(true), CodexFamilyDesign.ProjectTool(), CodexParametricDesign.Tool(), CodexParametricDesign.Tool(true))
                    });
                    Check(thread["thread"]?["id"] != null, "real Codex: ephemeral thread and dynamic tools accepted");
                }
            }
            Console.WriteLine("All Codex protocol tests passed. No model turn or login was requested.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static int FakeServer()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        JToken firstId = null, callbackId = null;
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var request = JObject.Parse(line);
            string method = (string)request["method"];
            var id = request["id"];
            if (method == null && (string)id == "server-1") { Reply(callbackId, request["result"]); continue; }
            if (method == "initialized" || method == "hang") continue;
            if (method == "first") { firstId = id; continue; }
            if (method == "second") { Reply(id, new JObject { ["value"] = "second" }); Reply(firstId, new JObject { ["value"] = "first" }); continue; }
            if (method == "callback")
            {
                callbackId = id;
                Console.WriteLine(new JObject { ["id"] = "server-1", ["method"] = "test/request", ["params"] = new JObject() }.ToString(Newtonsoft.Json.Formatting.None));
                continue;
            }
            if (method == "fail")
            {
                Console.WriteLine(new JObject { ["id"] = id, ["error"] = new JObject { ["code"] = -1, ["message"] = "Expected test error" } }.ToString(Newtonsoft.Json.Formatting.None));
                continue;
            }
            if (method == "notify") Console.WriteLine("{\"method\":\"test/notification\",\"params\":{}}");
            Reply(id, request["params"] ?? new JObject());
        }
        return 0;
    }
    private static void Reply(JToken id, JToken result) => Console.WriteLine(new JObject { ["id"] = id, ["result"] = result }.ToString(Newtonsoft.Json.Formatting.None));
}

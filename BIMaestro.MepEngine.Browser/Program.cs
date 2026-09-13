using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using BIMaestro.VideoGames;

await WebAssemblyHostBuilder.CreateDefault(args).Build().RunAsync();

public static class MepBrowserEngine
{
    [JSInvokable]
    public static string Calculate(string json)
    {
        var settings = GameMepCalculation.JsonSettings;
        if (settings.Converters.Count == 0) settings.Converters.Add(new PortableGeometryConverter());
        var request = JsonConvert.DeserializeObject<GameMepCalculationRequest>(json, settings)!;
        var graph = GameMepCalculation.Run(request);
        return JsonConvert.SerializeObject(new { graph, reportText = GameMepImpactAnalyzer.ToText(graph) }, settings);
    }
}

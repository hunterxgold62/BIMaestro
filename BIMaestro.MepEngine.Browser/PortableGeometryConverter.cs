using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal sealed class PortableGeometryConverter : JsonConverter
{
    public override bool CanWrite => false;
    public override bool CanConvert(Type type) => type == typeof(Point3D) || type == typeof(Vector3D) || type == typeof(Color);
    public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (type == typeof(Color))
        {
            if (token is JObject obj) return new Color { A = (byte?)obj.GetValue("a", StringComparison.OrdinalIgnoreCase) ?? 255,
                R = (byte?)obj.GetValue("r", StringComparison.OrdinalIgnoreCase) ?? 40, G = (byte?)obj.GetValue("g", StringComparison.OrdinalIgnoreCase) ?? 190,
                B = (byte?)obj.GetValue("b", StringComparison.OrdinalIgnoreCase) ?? 230 };
            string hex = token.Type == JTokenType.String ? ((string)token)!.TrimStart('#') : "";
            if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return new Color { A = hex.Length == 8 ? (byte)(value >> 24) : (byte)255, R = (byte)(value >> 16), G = (byte)(value >> 8), B = (byte)value };
            return Color.FromRgb(40,190,230);
        }
        double[] xyz;
        if (token is JObject point) xyz = new[] { "x", "y", "z" }.Select(key => (double?)point.GetValue(key, StringComparison.OrdinalIgnoreCase) ?? 0).ToArray();
        else if (token is JArray array) xyz = array.Select(v => (double)v).ToArray();
        else if (token.Type == JTokenType.String) xyz = ((string)token)!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        else xyz = new double[3];
        if (xyz.Length != 3 || xyz.Any(v => !double.IsFinite(v))) throw new JsonSerializationException("Coordonnées MEP invalides.");
        if (type == typeof(Point3D)) return new Point3D(xyz[0],xyz[1],xyz[2]);
        return new Vector3D(xyz[0],xyz[1],xyz[2]);
    }
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
}

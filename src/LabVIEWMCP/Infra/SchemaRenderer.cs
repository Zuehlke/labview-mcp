using System.Text;
using Pbr = Google.Protobuf.Reflection;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Renders reflected FileDescriptorProtos as a compact, diffable schema listing.
/// Kept separate from the tool so it can be exercised without a server: the point of the
/// summary is to spot drift between LabVIEW versions, which means its output shape matters.
/// </summary>
internal static class SchemaRenderer
{
    public static string RenderSummary(IEnumerable<Pbr.FileDescriptorProto> descriptors)
    {
        var sb = new StringBuilder();
        foreach (var fd in descriptors)
        {
            sb.AppendLine($"// {fd.Name}   package {fd.Package}   syntax {fd.Syntax}");

            foreach (var service in fd.Service)
            {
                sb.AppendLine($"service {service.Name} {{   // {service.Method.Count} rpcs");
                foreach (var method in service.Method)
                {
                    var input = (method.ClientStreaming ? "stream " : "") + Short(method.InputType);
                    var output = (method.ServerStreaming ? "stream " : "") + Short(method.OutputType);
                    sb.AppendLine($"  rpc {method.Name}({input}) returns ({output});");
                }
                sb.AppendLine("}");
            }

            foreach (var e in fd.EnumType)
                sb.AppendLine($"enum {e.Name} {{ " +
                              string.Join(", ", e.Value.Select(v => $"{v.Name}={v.Number}")) + " }");

            foreach (var message in fd.MessageType)
            {
                if (message.Options?.MapEntry == true) continue; // synthetic map entry
                var fields = message.Field.Select(FieldSignature);
                sb.AppendLine($"message {message.Name} {{ {string.Join("; ", fields)} }}");
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FieldSignature(Pbr.FieldDescriptorProto field)
    {
        var label = field.Label == Pbr.FieldDescriptorProto.Types.Label.Repeated ? "repeated " : "";
        var type = string.IsNullOrEmpty(field.TypeName)
            ? field.Type.ToString().Replace("Type", "").ToLowerInvariant()
            : Short(field.TypeName);
        return $"{label}{type} {field.Name}={field.Number}";
    }

    /// <summary>".lvai.FooRequest" -> "FooRequest". Fully qualified names bloat the listing.</summary>
    public static string Short(string typeName) => typeName.TrimStart('.').Split('.')[^1];
}

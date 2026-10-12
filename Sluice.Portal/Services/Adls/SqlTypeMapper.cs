using System.Text.Json;

namespace Sluice.Portal.Services.Adls;

public static class SqlTypeMapper
{
    public static string MapCdmType(JsonElement dataTypeElement, JsonElement? attributeElement = null)
    {
        var typeName = ResolveTypeName(dataTypeElement);
        if (typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase))
        {
            var (precision, scale) = ExtractDecimalPrecisionScale(attributeElement);
            return $"DECIMAL({precision},{scale})";
        }

        return typeName.ToLowerInvariant() switch
        {
            "string" => "NVARCHAR(MAX)",
            "guid" => "UNIQUEIDENTIFIER",
            "datetime" => "DATETIME2(7)",
            "datetimeoffset" => "DATETIMEOFFSET(7)",
            "boolean" => "BIT",
            "integer" or "biginteger" or "int64" => "BIGINT",
            "int32" => "INT",
            "int16" => "SMALLINT",
            "double" => "FLOAT",
            _ => "NVARCHAR(MAX)",
        };
    }

    private static string ResolveTypeName(JsonElement dataTypeElement)
    {
        if (dataTypeElement.ValueKind == JsonValueKind.String)
        {
            return dataTypeElement.GetString() ?? "string";
        }

        if (dataTypeElement.ValueKind == JsonValueKind.Array && dataTypeElement.GetArrayLength() > 0)
        {
            return dataTypeElement[0].GetString() ?? "string";
        }

        return "string";
    }

    private static (int Precision, int Scale) ExtractDecimalPrecisionScale(JsonElement? attributeElement)
    {
        if (attributeElement is null || attributeElement.Value.ValueKind != JsonValueKind.Object)
        {
            return (38, 6);
        }

        if (!attributeElement.Value.TryGetProperty("appliedTraits", out var traits))
        {
            return (38, 6);
        }

        foreach (var trait in traits.EnumerateArray())
        {
            if (!trait.TryGetProperty("traitReference", out var reference))
            {
                continue;
            }

            if (!reference.GetString()?.Contains("numeric.shaped", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            if (!trait.TryGetProperty("arguments", out var args))
            {
                continue;
            }

            var precision = 38;
            var scale = 6;
            foreach (var arg in args.EnumerateArray())
            {
                if (!arg.TryGetProperty("name", out var nameEl))
                {
                    continue;
                }

                var name = nameEl.GetString();
                if (!arg.TryGetProperty("value", out var valueEl))
                {
                    continue;
                }

                if (name == "precision" && valueEl.TryGetInt32(out var p))
                {
                    precision = p;
                }
                else if (name == "scale" && valueEl.TryGetInt32(out var s))
                {
                    scale = s;
                }
            }

            return (precision, scale);
        }

        return (38, 6);
    }
}
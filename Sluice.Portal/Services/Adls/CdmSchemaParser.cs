using System.Text.Json;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

public static class CdmSchemaParser
{
    public const string IdColumn = "Id";
    public const string WatermarkColumn = "SinkModifiedOn";

    // En F&O contiene el mismo valor que sysrowversion; desempata versiones con igual watermark.
    public const string VersionColumn = "versionnumber";

    // True en las filas de borrado del CSV incremental (cuerpo vacío, solo campos de sistema).
    public const string IsDeleteColumn = "IsDelete";

    public static CdmEntitySchema Parse(string entityName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("entities", out var entities))
        {
            return ParseV1(entityName, entities);
        }

        if (root.TryGetProperty("definitions", out var definitions))
        {
            return ParseV2(entityName, definitions);
        }

        throw new InvalidOperationException("Unsupported CDM schema format.");
    }

    private static CdmEntitySchema ParseV1(string entityName, JsonElement entities)
    {
        foreach (var entity in entities.EnumerateArray())
        {
            var name = entity.GetProperty("name").GetString();
            if (!string.Equals(name, entityName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attrs = new List<CdmAttribute>();
            foreach (var attr in entity.GetProperty("attributes").EnumerateArray())
            {
                var attrName = attr.GetProperty("name").GetString()!;
                attrs.Add(new CdmAttribute
                {
                    Name = attrName,
                    SqlType = SqlTypeMapper.MapCdmType(attr.GetProperty("dataType"), attr),
                    IsNullable = !string.Equals(attrName, IdColumn, StringComparison.OrdinalIgnoreCase),
                });
            }

            return InjectSystemColumns(new CdmEntitySchema { EntityName = entityName, Attributes = attrs });
        }

        throw new InvalidOperationException($"Entity '{entityName}' not found in CDM V1 schema.");
    }

    private static CdmEntitySchema ParseV2(string entityName, JsonElement definitions)
    {
        foreach (var def in definitions.EnumerateArray())
        {
            var name = def.GetProperty("entityName").GetString();
            if (!string.Equals(name, entityName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attrs = new List<CdmAttribute>();
            foreach (var attr in def.GetProperty("hasAttributes").EnumerateArray())
            {
                var attrName = attr.GetProperty("name").GetString()!;
                attrs.Add(new CdmAttribute
                {
                    Name = attrName,
                    SqlType = SqlTypeMapper.MapCdmType(attr.GetProperty("dataType"), attr),
                    IsNullable = !string.Equals(attrName, IdColumn, StringComparison.OrdinalIgnoreCase),
                });
            }

            return InjectSystemColumns(new CdmEntitySchema { EntityName = entityName, Attributes = attrs });
        }

        throw new InvalidOperationException($"Entity '{entityName}' not found in CDM V2 schema.");
    }

    public static CdmEntitySchema InjectSystemColumns(CdmEntitySchema schema)
    {
        var attrs = schema.Attributes.ToList();
        if (!attrs.Any(a => string.Equals(a.Name, IdColumn, StringComparison.OrdinalIgnoreCase)))
        {
            attrs.Insert(0, new CdmAttribute { Name = IdColumn, SqlType = "UNIQUEIDENTIFIER", IsNullable = false });
        }

        if (!attrs.Any(a => string.Equals(a.Name, WatermarkColumn, StringComparison.OrdinalIgnoreCase)))
        {
            attrs.Add(new CdmAttribute { Name = WatermarkColumn, SqlType = "DATETIME2(7)", IsNullable = true });
        }

        return schema with { Attributes = attrs };
    }
}
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

public class CdmSchemaParserTests
{
    private const string V1 = """
        {
          "entities": [{
            "name": "account",
            "attributes": [
              { "name": "name", "dataType": "string" },
              { "name": "amount", "dataType": "decimal", "appliedTraits": [{
                "traitReference": "is.dataFormat.numeric.shaped",
                "arguments": [
                  { "name": "precision", "value": 18 },
                  { "name": "scale", "value": 2 }
                ]
              }]}
            ]
          }]
        }
        """;

    private const string V2 = """
        {
          "definitions": [{
            "entityName": "contact",
            "hasAttributes": [
              { "name": "fullname", "dataType": ["string"] },
              { "name": "active", "dataType": "boolean" }
            ]
          }]
        }
        """;

    [Fact]
    public void ParseV1_InjectsIdAndSinkModifiedOn()
    {
        var schema = CdmSchemaParser.Parse("account", V1);
        Assert.Equal("Id", schema.Attributes[0].Name);
        Assert.Equal("UNIQUEIDENTIFIER", schema.Attributes[0].SqlType);
        Assert.Equal("SinkModifiedOn", schema.Attributes[^1].Name);
        Assert.Contains(schema.Attributes, a => a.Name == "amount" && a.SqlType == "DECIMAL(18,2)");
    }

    [Fact]
    public void ParseV2_InjectsSystemColumns()
    {
        var schema = CdmSchemaParser.Parse("contact", V2);
        Assert.Equal("Id", schema.Attributes[0].Name);
        Assert.Equal("SinkModifiedOn", schema.Attributes[^1].Name);
        Assert.Contains(schema.Attributes, a => a.Name == "active" && a.SqlType == "BIT");
    }
}
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

public sealed class OptionsetMetadataReaderTests
{
    // OptionSetName,Option,IsUserLocalizedLabel,LocalizedLabelLanguageCode,LocalizedLabel,GlobalOptionSetName,EntityName,ExternalValue
    private const string Sample =
        "amountsource,0,False,1033,\"Amount\",,accountingdistribution,PrimaryAmount\n" +
        "amountsource,1,False,1033,\"Discount\",,accountingdistribution,Discount\n" +
        "status,1,False,1033,\"Inactive\",,benefitplan,InActive\n" +
        "sysdatastatecode,0,False,1033,\"Default\",,benefitplan,Default\n" +          // excluido
        "amountsource,0,False,1036,\"Montant\",,accountingdistribution,PrimaryAmount\n"; // idioma != 1033

    [Fact]
    public void Parse_GroupsByEntityAndColumn_UsingExternalValue()
    {
        var meta = OptionsetMetadataReader.Parse(Sample);

        Assert.True(meta.TryGetValue("accountingdistribution", out var cols));
        Assert.True(cols.TryGetValue("amountsource", out var map));
        Assert.Equal("PrimaryAmount", map[0]);
        Assert.Equal("Discount", map[1]);
    }

    [Fact]
    public void Parse_ExcludesSysDataStateCode()
    {
        var meta = OptionsetMetadataReader.Parse(Sample);

        Assert.True(meta.TryGetValue("benefitplan", out var cols));
        Assert.False(cols.ContainsKey("sysdatastatecode"));
        Assert.True(cols.ContainsKey("status"));
    }

    [Fact]
    public void Parse_KeepsOnlyLanguage1033()
    {
        var meta = OptionsetMetadataReader.Parse(Sample);

        // amountsource solo tiene 2 opciones en 1033; la fila 1036 (francés) se descarta.
        Assert.Equal(2, meta["accountingdistribution"]["amountsource"].Count);
    }

    [Fact]
    public void Parse_HandlesQuotedFieldsWithEmbeddedCommas()
    {
        const string csv = "amountsource,5,False,1033,\"Round-off, net\",,accountingdistribution,RoundOff\n";

        var meta = OptionsetMetadataReader.Parse(csv);

        Assert.Equal("RoundOff", meta["accountingdistribution"]["amountsource"][5]);
    }

    [Fact]
    public void Parse_FirstRowWinsOnDuplicateOption()
    {
        const string csv =
            "type,2,False,1033,\"Canceling\",,accountingdistribution,Adjusting\n" +
            "type,2,False,1033,\"Other\",,accountingdistribution,SomethingElse\n";

        var meta = OptionsetMetadataReader.Parse(csv);

        Assert.Equal("Adjusting", meta["accountingdistribution"]["type"][2]);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(OptionsetMetadataReader.Parse(string.Empty));
    }

    [Fact]
    public void Parse_StripsLeadingBom()
    {
        const string csv = "﻿amountsource,0,False,1033,\"Amount\",,accountingdistribution,PrimaryAmount\n";

        var meta = OptionsetMetadataReader.Parse(csv);

        Assert.Equal("PrimaryAmount", meta["accountingdistribution"]["amountsource"][0]);
    }
}

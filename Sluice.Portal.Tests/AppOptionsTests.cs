using Microsoft.Extensions.Configuration;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Tests;

public class AppOptionsTests
{
    private static AppOptions LoadWith(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        return AppOptionsConfiguration.Load(config);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void TranslateEnums_binds_from_TRANSLATE_ENUMS(string raw, bool expected)
    {
        Assert.Equal(expected, LoadWith(("TRANSLATE_ENUMS", raw)).TranslateEnums);
    }

    [Fact]
    public void TranslateEnums_defaults_to_false_when_unset()
    {
        Assert.False(LoadWith().TranslateEnums);
    }

    // Proxies de confianza para X-Forwarded-For/Proto: sin configurar, listas vacías (se
    // mantiene el comportamiento previo de confiar en cualquiera); con valores, se parsean.
    [Fact]
    public void KnownProxies_y_KnownNetworks_vacios_si_no_se_configuran()
    {
        var options = LoadWith();
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownNetworks);
    }

    [Fact]
    public void KnownProxies_y_KnownNetworks_se_parsean_separados_por_comas()
    {
        var options = LoadWith(("KNOWN_PROXIES", "10.0.0.1, 10.0.0.2"), ("KNOWN_NETWORKS", "172.16.0.0/12,fd00::/8"));

        Assert.Equal(["10.0.0.1", "10.0.0.2"], options.KnownProxies.Select(p => p.ToString()));
        Assert.Equal(["172.16.0.0/12", "fd00::/8"], options.KnownNetworks.Select(n => n.ToString()));
    }

    [Theory]
    [InlineData("KNOWN_PROXIES", "10.0.0.300")]
    [InlineData("KNOWN_PROXIES", "proxy.local")]
    [InlineData("KNOWN_NETWORKS", "10.0.0.0")]
    [InlineData("KNOWN_NETWORKS", "10.0.0.0/99")]
    public void KnownProxies_o_KnownNetworks_invalidos_fallan_al_arrancar(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadWith((key, value)));
        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("ingest")]
    [InlineData("SQL")]
    [InlineData("Reconcile")]
    public void WorkerStage_valido_se_acepta_sin_distinguir_mayusculas(string raw)
    {
        Assert.Equal(raw, LoadWith(("WORKER_STAGE", raw)).WorkerStage);
    }

    // Un WORKER_STAGE mal escrito hacía que el worker procesara las tres colas (y, al no
    // ser orquestador, nadie agendaba): ahora el arranque falla con un mensaje claro.
    [Theory]
    [InlineData("ingset")]
    [InlineData("7")]
    [InlineData("sql,ingest")]
    public void WorkerStage_invalido_falla_al_arrancar(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadWith(("WORKER_STAGE", raw)));
        Assert.Contains("WORKER_STAGE", ex.Message);
    }


    // El scheduler debe correr solo en el nodo orquestador. En monolito (local) el
    // único contenedor agenda y procesa. En distribuido el 'api' (sin WORKER_STAGE)
    // agenda; los workers (con stage) solo procesan su cola y NO agendan, para que
    // varias réplicas no creen jobs duplicados del mismo pipeline.
    [Fact]
    public void Local_mode_is_orchestrator()
    {
        var options = new AppOptions { ExecutionMode = "local" };
        Assert.True(options.IsOrchestrator);
    }

    [Fact]
    public void Distributed_api_without_stage_is_orchestrator()
    {
        var options = new AppOptions { ExecutionMode = "distributed", WorkerStage = null };
        Assert.True(options.IsOrchestrator);
    }

    [Fact]
    public void Distributed_worker_with_stage_is_not_orchestrator()
    {
        var options = new AppOptions { ExecutionMode = "distributed", WorkerStage = "ingest" };
        Assert.False(options.IsOrchestrator);
    }
}

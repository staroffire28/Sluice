using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// Id corto de la lista de Jobs y del detalle: el hash final de "job-{pipeline}-{fecha}-{hash}".
public sealed class JobIdLabelsTests
{
    [Theory]
    [InlineData("job-pipe-lean-20261011154231-b9fa1d", "b9fa1d")]
    [InlineData("job-pipe-lean-resync-20261008033936-19ff86", "19ff86")]
    [InlineData("job-pipe-lean-force-20261008033936-abcdef12", "abcdef12")]
    public void Ids_del_portal_muestran_solo_el_hash(string id, string expected) =>
        Assert.Equal(expected, JobIdLabels.Short(id));

    [Theory]
    [InlineData("job-ui-ok")]                         // pocos guiones
    [InlineData("job-pipe-lean-20261011154231-ab")]   // sufijo demasiado corto
    [InlineData("job-pipe-lean-20261011154231")]      // sin hash: el último tramo es la fecha
    public void Ids_con_otro_formato_se_muestran_enteros(string id) =>
        Assert.Equal(id, JobIdLabels.Short(id));
}

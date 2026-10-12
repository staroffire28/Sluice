using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

// OpenParquetReadAsync copiaba el parquet entero a un MemoryStream (consolidados de GB en la
// memoria del worker). Ahora va a un fichero temporal con seek que se borra al disponerlo.
public class AdlsClientTempFileTests
{
    [Fact]
    public async Task CopyToTempFile_returns_a_seekable_copy_at_position_zero()
    {
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();

        await using var copy = await AdlsClient.CopyToTempFileAsync(new MemoryStream(data));

        Assert.True(copy.CanSeek);
        Assert.Equal(0, copy.Position);
        Assert.Equal(data.Length, copy.Length);
        using var read = new MemoryStream();
        await copy.CopyToAsync(read);
        Assert.Equal(data, read.ToArray());
    }

    [Fact]
    public async Task CopyToTempFile_deletes_the_file_when_disposed()
    {
        var copy = await AdlsClient.CopyToTempFileAsync(new MemoryStream([1, 2, 3]));
        var path = Assert.IsType<FileStream>(copy).Name;
        Assert.True(File.Exists(path));

        await copy.DisposeAsync();

        Assert.False(File.Exists(path));
    }
}

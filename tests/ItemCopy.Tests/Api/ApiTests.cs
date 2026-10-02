using ItemCopy.Api;
using ItemCopy.Http;

namespace ItemCopy.Tests.Api;

public class ChunkInfoTests
{
    [Theory]
    [InlineData("attachment; filename=\"c0\"; IsMedia=true; ItemsProcessed=10; ItemsSkipped=2", true, 10, 2)]
    [InlineData("attachment; ismedia=\"False\"", false, null, null)]
    [InlineData("IsMedia=TRUE;ItemsProcessed=5", true, 5, null)]
    public void Parses_content_disposition(string header, bool isMedia, int? processed, int? skipped)
    {
        Assert.Equal(new ChunkInfo(isMedia, processed, skipped), ChunkInfo.Parse(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("attachment; filename=c0")]
    [InlineData("attachment; IsMedia=maybe")]
    public void Requires_is_media(string? header)
    {
        Assert.Throws<SitecoreApiException>(() => ChunkInfo.Parse(header));
    }
}

public class ItemTransferClientTests
{
    [Theory]
    [InlineData("https://h/sitecore/shell/api/v3/ItemsTransfer/transfers/abc-123", "abc-123")]
    [InlineData("https://h/sitecore/shell/api/v3/ItemsTransfer/sources/blobs/content-transfer-1.raif", "content-transfer-1.raif")]
    [InlineData("https://h/x/consumed.20261001%20120000%201.abc/", "consumed.20261001 120000 1.abc")]
    [InlineData("/relative/path/id-9?x=1", "id-9")]
    public void Takes_last_location_segment(string location, string expected)
    {
        Assert.Equal(expected, ItemTransferClient.LastSegment(new Uri(location, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void No_location_means_no_segment()
    {
        Assert.Null(ItemTransferClient.LastSegment(null));
    }
}

using ItemCopy.Config;

namespace ItemCopy.Tests.Config;

public class EnvFileTests
{
    [Fact]
    public void Parses_keys_values_comments_quotes_and_export()
    {
        var values = EnvFile.Parse("""
            # comment
            SITECORE_DEV_HOST=dev.sitecorecloud.io

              export SITECORE_DEV_CLIENT_ID = abc
            SITECORE_DEV_CLIENT_SECRET="s3cr#t=="
            SINGLE='quoted value'
            NOT_A_PAIR
            =novalue
            """.Replace("\r\n", "\n"));

        Assert.Equal("dev.sitecorecloud.io", values["SITECORE_DEV_HOST"]);
        Assert.Equal("abc", values["SITECORE_DEV_CLIENT_ID"]);
        Assert.Equal("s3cr#t==", values["SITECORE_DEV_CLIENT_SECRET"]);
        Assert.Equal("quoted value", values["SINGLE"]);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public void Keeps_hash_and_equals_inside_unquoted_values()
    {
        var values = EnvFile.Parse("SECRET=a#b=c\r\n");
        Assert.Equal("a#b=c", values["SECRET"]);
    }

    [Fact]
    public void Later_duplicate_wins()
    {
        var values = EnvFile.Parse("A=1\nA=2");
        Assert.Equal("2", values["A"]);
    }
}

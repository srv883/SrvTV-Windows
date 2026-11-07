using SrvTv.Data;
using SrvTv.Models;
using System.Linq;
using Xunit;

namespace SrvTv.Tests;

public sealed class M3UParserTests
{
    [Fact]
    public void ParsesMetadataAndCommentsBeforeUrl()
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:-1 tvg-id="News.in" tvg-logo="https://img/logo.png" group-title="News",Example News HD
            # comment between metadata and URL
            https://cdn.example/live/news.m3u8
            """;

        var channels = M3UParser.ParseM3UContent(playlist);

        Assert.Single(channels);
        Assert.Equal("Example News HD", channels[0].Name);
        Assert.Equal("News", channels[0].Category);
        Assert.Equal("https://img/logo.png", channels[0].Logo);
        Assert.Equal(StreamFormat.HLS, channels[0].StreamFormat);
    }

    [Fact]
    public void ChannelIdMatchesJavaHashContract()
    {
        var channel = M3UParser.ParseM3UContent(
            "#EXTINF:-1,Stable Channel\nhttps://cdn.example/live.m3u8"
        ).Single();

        Assert.Equal(
            $"{unchecked((uint)M3UParser.JavaHash("Stable Channel"))}_" +
            unchecked((uint)M3UParser.JavaHash("https://cdn.example/live.m3u8")),
            channel.Id
        );
    }
}

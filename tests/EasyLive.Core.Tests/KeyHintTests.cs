using EasyLive.Core;

namespace EasyLive.Core.Tests;

public class KeyHintTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcd-1234-efgh")]
    [InlineData("rtmps://a.rtmps.youtube.com:443/live2/abcd-1234")]
    public void No_hint_when_empty_or_valid(string raw) => Assert.Null(KeyValidator.Explain(raw));

    [Theory]
    [InlineData("rtmps://a.rtmps.youtube.com:443/live2")]
    [InlineData("rtmps://a.rtmps.youtube.com:443/live2/")]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/")]
    [InlineData("rtmp://a.rtmp.youtube.com")]
    public void Server_address_hint(string raw) =>
        Assert.Equal(KeyValidator.ServerAddressHint, KeyValidator.Explain(raw));

    [Theory]
    [InlineData("abcd efgh-1234")]
    [InlineData("abcd-1234\r\nefgh-5678")]
    public void Spaces_hint(string raw) =>
        Assert.Equal(KeyValidator.SpacesHint, KeyValidator.Explain(raw));

    [Theory]
    [InlineData("ab")]
    public void Generic_hint_for_too_short(string raw) =>
        Assert.Equal(KeyValidator.GenericHint, KeyValidator.Explain(raw));

    [Fact]
    public void Generic_hint_for_too_long() =>
        Assert.Equal(KeyValidator.GenericHint, KeyValidator.Explain(new string('a', 300)));
}

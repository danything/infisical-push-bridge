using System.Security.Cryptography;
using System.Text;
using InfisicalPushBridge;
using Xunit;

public class SignatureTests
{
    static string Sign(string body, string key, long ts)
    {
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body)));
        return $"t={ts};{hex}";
    }

    const string Body = """{"event":"secrets.modified","timestamp":1700000000000}""";

    [Fact]
    public void 正しい署名を受け入れる()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        Assert.True(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", now));
    }

    [Fact]
    public void 鍵が違えば拒否する()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        Assert.False(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "another", now));
    }

    [Fact]
    public void ボディが1バイトでも違えば拒否する()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        Assert.False(Signature.Verify(header, Encoding.UTF8.GetBytes(Body + " "), "sekret", now));
    }

    [Fact]
    public void 古すぎるタイムスタンプは正しい署名でも拒否する()
    {
        var then = 1700000000000L;
        var header = Sign(Body, "sekret", then);
        Assert.False(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", then + Signature.ToleranceMs + 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc;deadbeef")]
    [InlineData("t=1700000000000")]
    public void 形式が壊れていれば拒否する(string? header)
    {
        Assert.False(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", 1700000000000L));
    }
}

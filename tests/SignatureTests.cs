using System.Security.Cryptography;
using System.Text;
using InfisicalPushBridge;
using System.Threading.Tasks;

public class SignatureTests
{
    static string Sign(string body, string key, long ts)
    {
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body)));
        return $"t={ts};{hex}";
    }

    const string Body = """{"event":"secrets.modified","timestamp":1700000000000}""";

    [Test]
    public async Task 正しい署名を受け入れる()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        await Assert.That(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", now)).IsTrue();
    }

    [Test]
    public async Task 鍵が違えば拒否する()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        await Assert.That(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "another", now)).IsFalse();
    }

    [Test]
    public async Task ボディが1バイトでも違えば拒否する()
    {
        var now = 1700000000000L;
        var header = Sign(Body, "sekret", now);
        await Assert.That(Signature.Verify(header, Encoding.UTF8.GetBytes(Body + " "), "sekret", now)).IsFalse();
    }

    [Test]
    public async Task 古すぎるタイムスタンプは正しい署名でも拒否する()
    {
        var then = 1700000000000L;
        var header = Sign(Body, "sekret", then);
        await Assert.That(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", then + Signature.ToleranceMs + 1)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("garbage")]
    [Arguments("t=abc;deadbeef")]
    [Arguments("t=1700000000000")]
    public async Task 形式が壊れていれば拒否する(string? header)
    {
        await Assert.That(Signature.Verify(header, Encoding.UTF8.GetBytes(Body), "sekret", 1700000000000L)).IsFalse();
    }
}
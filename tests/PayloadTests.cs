using System.Text;
using InfisicalPushBridge;
using Xunit;

public class PayloadTests
{
    [Fact]
    public void 本物のイベントから環境とパスを取り出す()
    {
        var body = """{"event":"secrets.modified","project":{"workspaceId":"x","environment":"prod","secretPath":"/worklog/worklog-secrets"},"timestamp":1}""";
        var (env, path) = Payload.ExtractScope(Encoding.UTF8.GetBytes(body));
        Assert.Equal("prod", env);
        Assert.Equal("/worklog/worklog-secrets", path);
    }

    [Fact]
    public void environmentがオブジェクトの版でもslugを拾う()
    {
        var body = """{"project":{"environment":{"slug":"prod"},"secretPath":"/a"}}""";
        var (env, path) = Payload.ExtractScope(Encoding.UTF8.GetBytes(body));
        Assert.Equal("prod", env);
        Assert.Equal("/a", path);
    }

    [Fact]
    public void 形が想定外ならnullを返す_呼び出し側は全CR対象に倒す()
    {
        Assert.Equal((null, null), Payload.ExtractScope(Encoding.UTF8.GetBytes("""{"event":"test"}""")));
        Assert.Equal((null, null), Payload.ExtractScope(Encoding.UTF8.GetBytes("not json")));
    }
}

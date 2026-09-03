using System.Text;
using InfisicalPushBridge;
using System.Threading.Tasks;

public class PayloadTests
{
    [Test]
    public async Task 本物のイベントから環境とパスを取り出す()
    {
        var body = """{"event":"secrets.modified","project":{"workspaceId":"x","environment":"prod","secretPath":"/worklog/worklog-secrets"},"timestamp":1}""";
        var (env, path) = Payload.ExtractScope(Encoding.UTF8.GetBytes(body));
        await Assert.That(env).IsEqualTo("prod");
        await Assert.That(path).IsEqualTo("/worklog/worklog-secrets");
    }

    [Test]
    public async Task environmentがオブジェクトの版でもslugを拾う()
    {
        var body = """{"project":{"environment":{"slug":"prod"},"secretPath":"/a"}}""";
        var (env, path) = Payload.ExtractScope(Encoding.UTF8.GetBytes(body));
        await Assert.That(env).IsEqualTo("prod");
        await Assert.That(path).IsEqualTo("/a");
    }

    [Test]
    public async Task 形が想定外ならnullを返す_呼び出し側は全CR対象に倒す()
    {
        await Assert.That(Payload.ExtractScope(Encoding.UTF8.GetBytes("""{"event":"test"}"""))).IsEqualTo((null, null));
        await Assert.That(Payload.ExtractScope(Encoding.UTF8.GetBytes("not json"))).IsEqualTo((null, null));
    }
}
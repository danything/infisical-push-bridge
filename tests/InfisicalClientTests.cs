using System.Text;
using InfisicalPushBridge;
using System.Threading.Tasks;

public class InfisicalClientTests
{
    [Test]
    public async Task 一覧からキー名だけを拾う()
    {
        var json = """
        {"secrets":[
          {"secretKey":"a","secretValue":"<hidden-by-infisical>","secretValueHidden":true},
          {"secretKey":"b","secretValue":"<hidden-by-infisical>","secretValueHidden":true}
        ],"imports":[]}
        """;
        await Assert.That(InfisicalClient.ParseNames(Encoding.UTF8.GetBytes(json), "secrets", "secretKey"))
            .IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(InfisicalClient.ParseNames(Encoding.UTF8.GetBytes("""{"folders":[]}"""), "secrets", "secretKey").Count)
            .IsEqualTo(0);
    }

    [Test]
    public async Task エラーはmessageだけを短く取り出す()
    {
        await Assert.That(InfisicalClient.ErrorMessage(Encoding.UTF8.GetBytes("""{"statusCode":400,"message":"Secret already exists"}""")))
            .IsEqualTo("Secret already exists");
        await Assert.That(InfisicalClient.ErrorMessage(Encoding.UTF8.GetBytes("<html>"))).IsEqualTo("(本文なし)");
        await Assert.That(InfisicalClient.ErrorMessage(Encoding.UTF8.GetBytes($$"""{"message":"{{new string('x', 500)}}"}""")).Length)
            .IsEqualTo(201);
    }
}

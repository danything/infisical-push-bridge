using System.Text;
using InfisicalPushBridge;
using System.Threading.Tasks;

public class KubeClientTests
{
    [Test]
    public async Task list応答からsecretsScopeを認証方式に依らず取り出す()
    {
        var json = """
        {"items":[
          {"metadata":{"namespace":"worklog","name":"worklog-secrets"},
           "spec":{"authentication":{"kubernetesAuth":{"identityId":"x","secretsScope":{"envSlug":"prod","secretsPath":"/worklog/worklog-secrets","recursive":false}}}}},
          {"metadata":{"namespace":"a","name":"ua"},
           "spec":{"authentication":{"universalAuth":{"secretsScope":{"envSlug":"prod","secretsPath":"/a/a","recursive":true}}}}},
          {"metadata":{"namespace":"broken","name":"no-scope"},
           "spec":{"authentication":{"kubernetesAuth":{"identityId":"x"}}}}
        ]}
        """;
        var targets = KubeClient.ParseList(Encoding.UTF8.GetBytes(json));

        await Assert.That(targets.Count).IsEqualTo(2);
        await Assert.That(targets[0]).IsEqualTo(new Target("worklog", "worklog-secrets", "prod", "/worklog/worklog-secrets", false));
        await Assert.That(targets[1]).IsEqualTo(new Target("a", "ua", "prod", "/a/a", true));
    }
}
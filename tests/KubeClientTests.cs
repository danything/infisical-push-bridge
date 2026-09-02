using System.Text;
using InfisicalPushBridge;
using Xunit;

public class KubeClientTests
{
    [Fact]
    public void list応答からsecretsScopeを認証方式に依らず取り出す()
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

        Assert.Equal(2, targets.Count);
        Assert.Equal(new Target("worklog", "worklog-secrets", "prod", "/worklog/worklog-secrets", false), targets[0]);
        Assert.Equal(new Target("a", "ua", "prod", "/a/a", true), targets[1]);
    }
}

using InfisicalPushBridge;
using System.Threading.Tasks;

public class MatchingTests
{
    [Test]
    public async Task パスと環境が一致すれば対象()
    {
        await Assert.That(Matching.Matches("prod", "/worklog/worklog-secrets", "prod", "/worklog/worklog-secrets", false)).IsTrue();
    }

    [Test]
    public async Task 環境が違えば対象外()
    {
        await Assert.That(Matching.Matches("staging", "/a", "prod", "/a", false)).IsFalse();
    }

    [Test]
    public async Task 別のパスは対象外_これが無いと毎回全アプリが再同期される()
    {
        await Assert.That(Matching.Matches("prod", "/worklog/worklog-secrets", "prod", "/mattermost/mattermost", false)).IsFalse();
    }

    [Test]
    public async Task イベント側が不明なら全部対象_余分に同期する方向へ倒す()
    {
        await Assert.That(Matching.Matches(null, null, "prod", "/a/b", false)).IsTrue();
    }

    [Test]
    public async Task recursiveなCRは配下の変更でも対象()
    {
        await Assert.That(Matching.Matches("prod", "/shared/entra", "prod", "/shared", true)).IsTrue();
        await Assert.That(Matching.Matches("prod", "/sharedother/x", "prod", "/shared", true)).IsFalse();
        await Assert.That(Matching.Matches("prod", "/anything", "prod", "/", true)).IsTrue();
    }

    [Test]
    public async Task 末尾スラッシュの揺れを吸収する()
    {
        await Assert.That(Matching.Matches("prod", "/a/b/", "prod", "/a/b", false)).IsTrue();
        await Assert.That(Matching.Normalize("a/b/")).IsEqualTo("/a/b");
        await Assert.That(Matching.Normalize("/")).IsEqualTo("/");
    }
}
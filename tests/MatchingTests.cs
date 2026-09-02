using InfisicalPushBridge;
using Xunit;

public class MatchingTests
{
    [Fact]
    public void パスと環境が一致すれば対象()
    {
        Assert.True(Matching.Matches("prod", "/worklog/worklog-secrets", "prod", "/worklog/worklog-secrets", false));
    }

    [Fact]
    public void 環境が違えば対象外()
    {
        Assert.False(Matching.Matches("staging", "/a", "prod", "/a", false));
    }

    [Fact]
    public void 別のパスは対象外_これが無いと毎回全アプリが再同期される()
    {
        Assert.False(Matching.Matches("prod", "/worklog/worklog-secrets", "prod", "/mattermost/mattermost", false));
    }

    [Fact]
    public void イベント側が不明なら全部対象_余分に同期する方向へ倒す()
    {
        Assert.True(Matching.Matches(null, null, "prod", "/a/b", false));
    }

    [Fact]
    public void recursiveなCRは配下の変更でも対象()
    {
        Assert.True(Matching.Matches("prod", "/shared/entra", "prod", "/shared", true));
        Assert.False(Matching.Matches("prod", "/sharedother/x", "prod", "/shared", true));
        Assert.True(Matching.Matches("prod", "/anything", "prod", "/", true));
    }

    [Fact]
    public void 末尾スラッシュの揺れを吸収する()
    {
        Assert.True(Matching.Matches("prod", "/a/b/", "prod", "/a/b", false));
        Assert.Equal("/a/b", Matching.Normalize("a/b/"));
        Assert.Equal("/", Matching.Normalize("/"));
    }
}

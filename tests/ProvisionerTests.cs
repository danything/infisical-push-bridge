using InfisicalPushBridge;
using System.Threading.Tasks;

/// <summary>Infisical の代わり。フォルダとキーをメモリに持ち、呼ばれた操作を記録する。</summary>
sealed class FakeInfisical : IInfisical
{
    public readonly HashSet<string> Folders = ["/"];
    public readonly Dictionary<string, Dictionary<string, string>> Secrets = new();
    public readonly List<string> Calls = [];
    public Func<string, Exception?> FailCreateSecret = _ => null;

    public Task<IReadOnlySet<string>> ListFolderNames(string env, string parent)
    {
        Calls.Add($"list-folders {parent}");
        var prefix = parent == "/" ? "/" : parent + "/";
        IReadOnlySet<string> names = Folders
            .Where(f => f != "/" && f.StartsWith(prefix) && !f[prefix.Length..].Contains('/'))
            .Select(f => f[prefix.Length..]).ToHashSet();
        return Task.FromResult(names);
    }

    public Task CreateFolder(string env, string parent, string name)
    {
        Calls.Add($"create-folder {parent} {name}");
        if (!Folders.Contains(parent)) throw new InvalidOperationException("親が無い");
        Folders.Add(Provisioning.Join(parent, name));
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> ListSecretKeys(string env, string folder)
    {
        Calls.Add($"list-keys {folder}");
        IReadOnlySet<string> keys = Secrets.TryGetValue(folder, out var s) ? s.Keys.ToHashSet() : new HashSet<string>();
        return Task.FromResult(keys);
    }

    public Task CreateSecret(string env, string folder, string key, string value, string comment)
    {
        Calls.Add($"create-key {folder} {key}");
        if (FailCreateSecret(key) is { } ex) throw ex;
        var s = Secrets.TryGetValue(folder, out var existing) ? existing : Secrets[folder] = new();
        // 本物の Infisical と同じく、既にあれば拒否する
        if (!s.TryAdd(key, value)) throw new InvalidOperationException("Secret already exists");
        return Task.CompletedTask;
    }
}

public class ProvisionerTests
{
    static Target Cr(string path, string? refs = null, string slug = "doa", string env = "prod") =>
        new("matrix", "matrix", env, path, false, slug, refs);

    static Provisioner New(FakeInfisical fake, List<string>? log = null) =>
        new(fake, "doa", s => log?.Add(s));

    [Test]
    public async Task 無いフォルダを上から順に作る()
    {
        var fake = new FakeInfisical();
        fake.Folders.Add("/matrix");

        var result = await New(fake).Reconcile(Cr("/matrix/matrix/sub"));

        await Assert.That(result.FoldersCreated).IsEqualTo(2);
        await Assert.That(fake.Folders).Contains("/matrix/matrix/sub");
        await Assert.That(fake.Calls).IsEquivalentTo(new[]
        {
            "list-folders /",
            "list-folders /matrix",
            "create-folder /matrix matrix",
            // 親を作った直後は一覧を取らずに作る
            "create-folder /matrix/matrix sub",
        });
    }

    [Test]
    public async Task フォルダが揃っていれば何も作らない_何度回しても同じ()
    {
        var fake = new FakeInfisical();
        fake.Folders.UnionWith(["/matrix", "/matrix/matrix"]);
        var p = New(fake);

        var first = await p.Reconcile(Cr("/matrix/matrix"));
        var second = await p.Reconcile(Cr("/matrix/matrix"));

        await Assert.That(first.Changed).IsFalse();
        await Assert.That(second.Changed).IsFalse();
        await Assert.That(fake.Calls.Any(c => c.StartsWith("create"))).IsFalse();
    }

    [Test]
    public async Task 足りないキーだけ参照で作り_既にあるキーは触らない()
    {
        var fake = new FakeInfisical();
        fake.Folders.UnionWith(["/matrix", "/matrix/matrix"]);
        fake.Secrets["/matrix/matrix"] = new() { ["oidc-client-secret"] = "手で入れた値" };

        var result = await New(fake).Reconcile(Cr("/matrix/matrix",
            "oidc-client-secret=/shared/entra/client-secret, smtp-password=/shared/smtp/password"));

        await Assert.That(result.KeysCreated).IsEquivalentTo(new[] { "smtp-password" });
        await Assert.That(fake.Secrets["/matrix/matrix"]["oidc-client-secret"]).IsEqualTo("手で入れた値");
        await Assert.That(fake.Secrets["/matrix/matrix"]["smtp-password"]).IsEqualTo("${prod.shared.smtp.password}");
        await Assert.That(fake.Calls.Contains("create-key /matrix/matrix oidc-client-secret")).IsFalse();
    }

    [Test]
    public async Task 新しいフォルダを作ってから参照を入れる()
    {
        var fake = new FakeInfisical();

        var result = await New(fake).Reconcile(Cr("/denpa/denpa-oidc", "client-secret=/shared/entra/client-secret"));

        await Assert.That(result.FoldersCreated).IsEqualTo(2);
        await Assert.That(fake.Secrets["/denpa/denpa-oidc"]["client-secret"]).IsEqualTo("${prod.shared.entra.client-secret}");
    }

    [Test]
    public async Task 一つ作れなくても残りは作る()
    {
        var fake = new FakeInfisical { FailCreateSecret = k => k == "a" ? new InvalidOperationException("403") : null };
        fake.Folders.Add("/x");
        var log = new List<string>();

        var result = await New(fake, log).Reconcile(Cr("/x", "a=/shared/a, b=/shared/b"));

        await Assert.That(result.KeysCreated).IsEquivalentTo(new[] { "b" });
        await Assert.That(result.Problems.Count).IsEqualTo(1);
        // ログに値(参照の文字列)は出さない
        await Assert.That(log.Any(l => l.Contains("${"))).IsFalse();
    }

    [Test]
    public async Task 別のプロジェクトのCRは触らない()
    {
        var fake = new FakeInfisical();
        var result = await New(fake).Reconcile(Cr("/a/b", "k=/s/k", slug: "other"));
        await Assert.That(result.Changed).IsFalse();
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task 作れないパスは触らずに報告する()
    {
        var fake = new FakeInfisical();
        var result = await New(fake).Reconcile(Cr("/a b/c"));
        await Assert.That(result.Problems.Count).IsEqualTo(1);
        await Assert.That(fake.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task 新顔と変わったCRだけ選び_resyncでは全部選ぶ()
    {
        var seen = new Dictionary<(string, string), Target>();
        var a = new Target("ns", "a", "prod", "/a", false, "doa");
        var b = new Target("ns", "b", "prod", "/b", false, "doa");

        await Assert.That(ProvisionLoop.SelectDue([a, b], seen, full: false).Count).IsEqualTo(2);
        await Assert.That(ProvisionLoop.SelectDue([a, b], seen, full: false).Count).IsEqualTo(0);

        var b2 = b with { References = "k=/s/k" };
        await Assert.That(ProvisionLoop.SelectDue([a, b2], seen, full: false)).IsEquivalentTo(new[] { b2 });
        await Assert.That(ProvisionLoop.SelectDue([a, b2], seen, full: true).Count).IsEqualTo(2);

        // 消えた CR は忘れる
        ProvisionLoop.SelectDue([a], seen, full: false);
        await Assert.That(seen.ContainsKey(("ns", "b"))).IsFalse();
    }
}

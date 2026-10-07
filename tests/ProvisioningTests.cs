using InfisicalPushBridge;
using System.Threading.Tasks;

public class ProvisioningTests
{
    [Test]
    public async Task パスを部品に分ける()
    {
        await Assert.That(Provisioning.SplitPath("/matrix/matrix")).IsEquivalentTo(new[] { "matrix", "matrix" });
        await Assert.That(Provisioning.SplitPath("a/b/c/")).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(Provisioning.SplitPath("/")!.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("/a b/c")]
    [Arguments("/a.b")]
    [Arguments("/a/${x}")]
    public async Task フォルダ名に使えない文字があればnull_作らずに飛ばす(string path)
    {
        await Assert.That(Provisioning.SplitPath(path)).IsNull();
    }

    [Test]
    public async Task 作るフォルダを上から順に並べる()
    {
        var chain = Provisioning.FolderChain(["infisical-push-bridge", "infisical-push-bridge", "x"]).ToList();
        await Assert.That(chain).IsEquivalentTo(new[]
        {
            ("/", "infisical-push-bridge"),
            ("/infisical-push-bridge", "infisical-push-bridge"),
            ("/infisical-push-bridge/infisical-push-bridge", "x"),
        });
    }

    [Test]
    public async Task 注釈をカンマと改行で区切って読む()
    {
        var (refs, errors) = Provisioning.ParseReferences(
            "oidc-client-secret=/shared/entra/client-secret, smtp-password = /shared/smtp/password\n  token=/root-key  \n");
        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(refs).IsEquivalentTo(new[]
        {
            new Reference("oidc-client-secret", "/shared/entra", "client-secret"),
            new Reference("smtp-password", "/shared/smtp", "password"),
            new Reference("token", "/", "root-key"),
        });
    }

    [Test]
    public async Task 注釈が無ければ何もしない()
    {
        await Assert.That(Provisioning.ParseReferences(null).References.Count).IsEqualTo(0);
        await Assert.That(Provisioning.ParseReferences("  ").References.Count).IsEqualTo(0);
    }

    [Test]
    public async Task 壊れた項目だけ飛ばして残りは使う()
    {
        var (refs, errors) = Provisioning.ParseReferences(
            "ok=/shared/a/b, no-equals, bad key=/s/k, rel=shared/k, dot=/shared/a.b, dup=/s/one, dup=/s/two, empty=/");
        await Assert.That(refs).IsEquivalentTo(new[]
        {
            new Reference("ok", "/shared/a", "b"),
            new Reference("dup", "/s", "one"),
        });
        await Assert.That(errors.Count).IsEqualTo(6);
    }

    [Test]
    public async Task 参照はInfisicalの構文で書く()
    {
        await Assert.That(Provisioning.FormatReference("prod", "/shared/entra", "client-secret"))
            .IsEqualTo("${prod.shared.entra.client-secret}");
        await Assert.That(Provisioning.FormatReference("prod", "/", "KEY")).IsEqualTo("${prod.KEY}");
        await Assert.That(Provisioning.FormatReference("dev", "a/b/", "K")).IsEqualTo("${dev.a.b.K}");
    }

    [Test]
    public async Task 既にあるキーは決して選ばない()
    {
        var refs = new[]
        {
            new Reference("oidc-client-secret", "/shared/entra", "client-secret"),
            new Reference("smtp-password", "/shared/smtp", "password"),
        };
        var plan = Provisioning.PlanMissing(refs, new HashSet<string> { "oidc-client-secret" }, "prod", "/matrix/matrix");
        await Assert.That(plan).IsEquivalentTo(new[] { ("smtp-password", "${prod.shared.smtp.password}") });
    }

    [Test]
    public async Task 自分自身を指す参照は作らない()
    {
        var plan = Provisioning.PlanMissing(
            [new Reference("k", "/a/b", "k"), new Reference("other", "/a/b", "k")],
            new HashSet<string>(), "prod", "/a/b/");
        await Assert.That(plan).IsEquivalentTo(new[] { ("other", "${prod.a.b.k}") });
    }
}

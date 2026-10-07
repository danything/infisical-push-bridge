using InfisicalPushBridge;

var secretKey = Environment.GetEnvironmentVariable("WEBHOOK_SECRET");
if (string.IsNullOrEmpty(secretKey))
{
    Console.Error.WriteLine("WEBHOOK_SECRET が未設定です。Infisical の Webhook に設定した secret key と同じ値を渡してください。");
    Environment.Exit(1);
}

var builder = WebApplication.CreateSlimBuilder(args);
var kube = new KubeClient();

// フォルダの自動作成と参照の自動投入(既定は無効)。Infisical に書けるアイデンティティが要る
if (Environment.GetEnvironmentVariable("PROVISION_ENABLED") == "true")
{
    string Need(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(v)) return v;
        Console.Error.WriteLine($"PROVISION_ENABLED=true なのに {name} が未設定です。");
        Environment.Exit(1);
        return "";
    }
    string? Opt(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    int Seconds(string name, int fallback) => int.TryParse(Opt(name), out var s) && s > 0 ? s : fallback;

    var method = Opt("INFISICAL_AUTH_METHOD") ?? "kubernetes";
    if (method is not ("kubernetes" or "universal"))
    {
        Console.Error.WriteLine($"INFISICAL_AUTH_METHOD は kubernetes か universal です(今: {method})。");
        Environment.Exit(1);
    }
    var options = new InfisicalOptions(
        Need("INFISICAL_API_URL"),
        Need("INFISICAL_PROJECT_ID"),
        method,
        method == "kubernetes" ? Need("INFISICAL_IDENTITY_ID") : null,
        Opt("INFISICAL_TOKEN_PATH") ?? Path.Combine(KubeClient.SaDir, "token"),
        method == "universal" ? Need("INFISICAL_CLIENT_ID") : null,
        method == "universal" ? Need("INFISICAL_CLIENT_SECRET") : null);

    var provisioner = new Provisioner(new InfisicalClient(options), Need("INFISICAL_PROJECT_SLUG"), Console.WriteLine);
    builder.Services.AddHostedService(_ => new ProvisionLoop(kube, provisioner,
        TimeSpan.FromSeconds(Seconds("PROVISION_POLL_SECONDS", 30)),
        TimeSpan.FromSeconds(Seconds("PROVISION_RESYNC_SECONDS", 600))));
}

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost("/", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    if (!Signature.Verify(req.Headers["x-infisical-signature"].ToString(), body, secretKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
    {
        // 署名の無い/壊れた要求は誰でも送れる。理由は返さない
        Console.WriteLine("[bridge] 署名検証に失敗した要求を拒否");
        return Results.Unauthorized();
    }

    var (env, path) = Payload.ExtractScope(body);
    var all = await kube.ListInfisicalSecrets();
    var matched = all.Where(t => Matching.Matches(env, path, t.EnvSlug, t.SecretsPath, t.Recursive)).ToList();

    var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    foreach (var t in matched)
        await kube.Annotate(t.Namespace, t.Name, stamp);

    Console.WriteLine($"[bridge] env={env ?? "(不明)"} path={path ?? "(不明)"} -> {matched.Count}/{all.Count} 件を即時リコンサイル");
    return Results.NoContent();
});

app.Run();

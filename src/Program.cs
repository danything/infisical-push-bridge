using InfisicalPushBridge;

var secretKey = Environment.GetEnvironmentVariable("WEBHOOK_SECRET");
if (string.IsNullOrEmpty(secretKey))
{
    Console.Error.WriteLine("WEBHOOK_SECRET が未設定です。Infisical の Webhook に設定した secret key と同じ値を渡してください。");
    Environment.Exit(1);
}

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var kube = new KubeClient();

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

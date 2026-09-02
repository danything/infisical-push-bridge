using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace InfisicalPushBridge;

public readonly record struct Target(string Namespace, string Name, string EnvSlug, string SecretsPath, bool Recursive);

/// <summary>
/// クラスタ内から k8s API を叩く最小クライアント。
/// 資格情報は Pod にマウントされる ServiceAccount のトークンと CA だけを使う。
/// トークンは短命でローテーションされるので、**リクエストごとにファイルから読み直す**。
/// </summary>
public sealed class KubeClient
{
    const string SaDir = "/var/run/secrets/kubernetes.io/serviceaccount";
    const string ApiGroup = "apis/secrets.infisical.com/v1alpha1";

    readonly HttpClient _http;
    readonly string _base;
    readonly string _saDir;

    public KubeClient(string? saDir = null)
    {
        _saDir = saDir ?? SaDir;
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "kubernetes.default.svc";
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        _base = $"https://{host}:{port}";

        var handler = new HttpClientHandler();
        var caPath = Path.Combine(_saDir, "ca.crt");
        if (File.Exists(caPath))
        {
            // クラスタ CA だけを信頼する(システムのルート証明書には入っていないため)
            var ca = new X509Certificate2Collection();
            ca.ImportFromPemFile(caPath);
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, _) =>
            {
                if (cert is null || chain is null) return false;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    string Token() => File.ReadAllText(Path.Combine(_saDir, "token")).Trim();

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{_base}/{path}");
        req.Headers.Authorization = new("Bearer", Token());
        return req;
    }

    public async Task<List<Target>> ListInfisicalSecrets()
    {
        using var res = await _http.SendAsync(Request(HttpMethod.Get, $"{ApiGroup}/infisicalsecrets?limit=500"));
        res.EnsureSuccessStatusCode();
        return ParseList(await res.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// list 応答から突き合わせに要る部分だけを取り出す。認証方式(kubernetesAuth 等)は
    /// CR ごとに違いうるので、authentication 直下を総当たりして secretsScope を探す。
    /// </summary>
    public static List<Target> ParseList(ReadOnlySpan<byte> json)
    {
        var targets = new List<Target>();
        using var doc = JsonDocument.Parse(json.ToArray());
        if (!doc.RootElement.TryGetProperty("items", out var items)) return targets;

        foreach (var item in items.EnumerateArray())
        {
            var meta = item.GetProperty("metadata");
            if (!item.TryGetProperty("spec", out var spec)) continue;
            if (!spec.TryGetProperty("authentication", out var auth)) continue;

            foreach (var method in auth.EnumerateObject())
            {
                if (method.Value.ValueKind != JsonValueKind.Object) continue;
                if (!method.Value.TryGetProperty("secretsScope", out var scope)) continue;

                targets.Add(new Target(
                    meta.GetProperty("namespace").GetString()!,
                    meta.GetProperty("name").GetString()!,
                    scope.TryGetProperty("envSlug", out var env) ? env.GetString() ?? "" : "",
                    scope.TryGetProperty("secretsPath", out var p) ? p.GetString() ?? "/" : "/",
                    scope.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True));
                break;
            }
        }
        return targets;
    }

    /// <summary>
    /// 注釈を1つ書き換えて operator のリコンサイルを即時に走らせる。
    /// CR の watch イベントで動くので、resyncInterval を待たずに同期される。
    /// </summary>
    public async Task Annotate(string ns, string name, string value)
    {
        var req = Request(HttpMethod.Patch, $"{ApiGroup}/namespaces/{ns}/infisicalsecrets/{name}");
        // value はタイムスタンプ(数字のみ)なので連結で安全に組める
        var patch = "{\"metadata\":{\"annotations\":{\"push-bridge.doany.io/event-at\":\"" + value + "\"}}}";
        req.Content = new StringContent(patch, Encoding.UTF8, "application/merge-patch+json");
        using var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }
}

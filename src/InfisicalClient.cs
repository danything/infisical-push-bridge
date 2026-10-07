using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InfisicalPushBridge;

/// <summary>
/// Infisical に対してブリッジがする操作の全部。**更新と削除はここに無い** ──
/// 既存のフォルダやキーを書き換える経路がそもそも存在しない。
/// </summary>
public interface IInfisical
{
    /// <summary><paramref name="parent"/> 直下のフォルダ名。親が無ければ空。</summary>
    Task<IReadOnlySet<string>> ListFolderNames(string env, string parent);

    /// <summary>フォルダを 1 つ作る。既にあれば(競合)何もしない。</summary>
    Task CreateFolder(string env, string parent, string name);

    /// <summary>フォルダにあるキー名。**値は要求しない**(viewSecretValue=false)。</summary>
    Task<IReadOnlySet<string>> ListSecretKeys(string env, string folder);

    /// <summary>キーを新規作成する。既にあれば Infisical が拒否する(上書きはしない)。</summary>
    Task CreateSecret(string env, string folder, string key, string value, string comment);
}

public sealed class InfisicalException(HttpStatusCode status, string message)
    : Exception($"Infisical が {(int)status} を返した: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string ServerMessage { get; } = message;
}

public sealed record InfisicalOptions(
    string ApiUrl,
    string ProjectId,
    string AuthMethod,
    string? IdentityId,
    string TokenPath,
    string? ClientId,
    string? ClientSecret);

/// <summary>
/// Infisical の REST API(v0.165 系で確認)を叩く最小クライアント。
/// ログインはブリッジ自身のマシンアイデンティティ(Kubernetes 認証か Universal Auth)。
/// アクセストークンはメモリにだけ持ち、期限の少し前か 401 で取り直す。
/// トークン・JWT・シークレットの値はログに出さない。
/// </summary>
public sealed class InfisicalClient : IInfisical
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly InfisicalOptions _o;
    readonly SemaphoreSlim _loginLock = new(1, 1);
    string? _token;
    DateTimeOffset _expiresAt;

    public InfisicalClient(InfisicalOptions options)
    {
        _o = options with { ApiUrl = options.ApiUrl.TrimEnd('/') };
    }

    async Task<string> AccessToken(bool force)
    {
        await _loginLock.WaitAsync();
        try
        {
            if (!force && _token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;

            HttpRequestMessage req;
            if (_o.AuthMethod == "universal")
            {
                req = new(HttpMethod.Post, $"{_o.ApiUrl}/v1/auth/universal-auth/login")
                {
                    Content = Json(w =>
                    {
                        w.WriteString("clientId", _o.ClientId);
                        w.WriteString("clientSecret", _o.ClientSecret);
                    })
                };
            }
            else
            {
                // Pod にマウントされる ServiceAccount のトークン(短命・自動ローテーション)を毎回読み直す。
                // Infisical はこのトークン自身で TokenReview を呼ぶので、SA に system:auth-delegator が要る
                var jwt = (await File.ReadAllTextAsync(_o.TokenPath)).Trim();
                req = new(HttpMethod.Post, $"{_o.ApiUrl}/v1/auth/kubernetes-auth/login")
                {
                    Content = Json(w =>
                    {
                        w.WriteString("identityId", _o.IdentityId);
                        w.WriteString("jwt", jwt);
                    })
                };
            }

            using var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsByteArrayAsync();
            if (!res.IsSuccessStatusCode) throw new InfisicalException(res.StatusCode, ErrorMessage(body));

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("accessToken").GetString();
            var ttl = doc.RootElement.TryGetProperty("expiresIn", out var e) && e.TryGetInt64(out var s) ? s : 300;
            // 期限ぎりぎりで使わないよう、残り 1 割(最低 30 秒)を残して取り直す
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, ttl - Math.Max(30, ttl / 10)));
            return _token!;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    async Task<byte[]> Send(Func<HttpRequestMessage> build)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = build();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(force: attempt > 0));
            using var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsByteArrayAsync();
            if (res.IsSuccessStatusCode) return body;
            // トークンが失効・取り消されていたら 1 回だけ取り直す
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            throw new InfisicalException(res.StatusCode, ErrorMessage(body));
        }
    }

    string Query(string env, string pathKey, string path, string extra = "") =>
        $"projectId={Uri.EscapeDataString(_o.ProjectId)}&environment={Uri.EscapeDataString(env)}&{pathKey}={Uri.EscapeDataString(path)}{extra}";

    public async Task<IReadOnlySet<string>> ListFolderNames(string env, string parent)
    {
        var body = await Send(() => new(HttpMethod.Get, $"{_o.ApiUrl}/v2/folders?{Query(env, "path", parent)}"));
        return ParseNames(body, "folders", "name");
    }

    public async Task CreateFolder(string env, string parent, string name)
    {
        try
        {
            await Send(() => new(HttpMethod.Post, $"{_o.ApiUrl}/v2/folders")
            {
                Content = Json(w =>
                {
                    w.WriteString("projectId", _o.ProjectId);
                    w.WriteString("environment", env);
                    w.WriteString("name", name);
                    w.WriteString("path", parent);
                })
            });
        }
        catch (InfisicalException ex) when (ex.Status == HttpStatusCode.BadRequest &&
                                            ex.ServerMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // 一覧を見てから作るまでの間に誰かが作った。目的は果たしている
        }
    }

    public async Task<IReadOnlySet<string>> ListSecretKeys(string env, string folder)
    {
        // viewSecretValue=false: 値は "<hidden-by-infisical>" に伏せられて返る(DescribeSecret 権限だけで足りる)。
        // 参照の展開も import も要らない。読むのは secretKey だけ
        var body = await Send(() => new(HttpMethod.Get,
            $"{_o.ApiUrl}/v4/secrets?{Query(env, "secretPath", folder,
                "&viewSecretValue=false&expandSecretReferences=false&includeImports=false&recursive=false")}"));
        return ParseNames(body, "secrets", "secretKey");
    }

    public Task CreateSecret(string env, string folder, string key, string value, string comment) =>
        Send(() => new(HttpMethod.Post, $"{_o.ApiUrl}/v4/secrets/{Uri.EscapeDataString(key)}")
        {
            Content = Json(w =>
            {
                w.WriteString("projectId", _o.ProjectId);
                w.WriteString("environment", env);
                w.WriteString("secretPath", folder);
                w.WriteString("secretValue", value);
                w.WriteString("secretComment", comment);
                w.WriteString("type", "shared");
            })
        });

    /// <summary>応答の配列から 1 つのフィールドだけを拾う(ほかのフィールドは読まない)。</summary>
    public static IReadOnlySet<string> ParseNames(ReadOnlySpan<byte> json, string arrayName, string field)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json.ToArray());
        if (!doc.RootElement.TryGetProperty(arrayName, out var items) || items.ValueKind != JsonValueKind.Array) return names;
        foreach (var item in items.EnumerateArray())
            if (item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                names.Add(v.GetString()!);
        return names;
    }

    /// <summary>エラー応答の message だけを短く取り出す(ボディを丸ごとログに出さない)。</summary>
    public static string ErrorMessage(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var s = m.GetString() ?? "";
                return s.Length > 200 ? s[..200] + "…" : s;
            }
        }
        catch (JsonException)
        {
        }
        return "(本文なし)";
    }

    static StringContent Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        var content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
        return content;
    }
}

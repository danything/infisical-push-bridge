namespace InfisicalPushBridge;

/// <summary>注釈 1 項目ぶん。フォルダの <c>Key</c> を、<c>SourceFolder</c> の <c>SourceKey</c> への参照として作る。</summary>
public readonly record struct Reference(string Key, string SourceFolder, string SourceKey);

/// <summary>
/// フォルダの自動作成と、ほかの値を引き継ぐ参照の自動投入のための、副作用の無い部品。
/// ネットワークに触る側(<see cref="Provisioner"/>)から切り離して単体テストできるようにしてある。
/// </summary>
public static class Provisioning
{
    /// <summary>
    /// InfisicalSecret に付ける注釈。値は <c>キー=/フォルダ/…/元のキー</c> をカンマか改行で並べたもの。
    /// 例: <c>oidc-client-secret=/shared/entra/client-secret, smtp-password=/shared/smtp/password</c>
    /// </summary>
    public const string ReferencesAnnotation = "push-bridge.doany.io/references";

    /// <summary>
    /// Infisical のフォルダ名に使える文字(サーバの isValidFolderName と同じ: 英数字・ハイフン・アンダースコア)。
    /// 参照構文 <c>${env.a.b.KEY}</c> の部品(環境 slug・フォルダ名・元のキー)もこの文字だけなら確実に通る
    /// (サーバの正規表現は <c>[a-zA-Z0-9-_.]</c> で、<c>.</c> は区切りに使われるので部品には入れられない)。
    /// </summary>
    public static bool IsSegment(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// 作る側のキー名。Infisical はコロンとスラッシュを拒むので、それに加えて
    /// 注釈の区切り(<c>=</c> <c>,</c>)や空白も入らない保守的な集合にしておく。
    /// <c>.dockerconfigjson</c> のようなドット入りは作る側なら問題ない。
    /// </summary>
    public static bool IsKeyName(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// <c>/a/b/c</c> を <c>["a","b","c"]</c> に分ける。<c>/</c> は空配列。
    /// フォルダ名に使えない部品があれば null(作れないので触らない)。
    /// </summary>
    public static string[]? SplitPath(string path)
    {
        var segments = Matching.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(IsSegment) ? segments : null;
    }

    /// <summary>
    /// 上から順に「どの親の下に・どの名前のフォルダが要るか」を並べる。
    /// <c>["a","b"]</c> → <c>("/","a"), ("/a","b")</c>。
    /// </summary>
    public static IEnumerable<(string Parent, string Name)> FolderChain(IReadOnlyList<string> segments)
    {
        var parent = "/";
        foreach (var name in segments)
        {
            yield return (parent, name);
            parent = Join(parent, name);
        }
    }

    public static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    /// <summary>
    /// 注釈を読む。壊れた項目はエラーに積んで飛ばし、正しい項目だけ返す(1 つの書き間違いで全部止めない)。
    /// 同じキーが 2 回出たら最初を採る。
    /// </summary>
    public static (List<Reference> References, List<string> Errors) ParseReferences(string? annotation)
    {
        var refs = new List<Reference>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(annotation)) return (refs, errors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in annotation.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq < 0)
            {
                errors.Add($"'{raw}': キー=/フォルダ/元のキー の形ではない");
                continue;
            }

            var key = raw[..eq].Trim();
            var source = raw[(eq + 1)..].Trim();

            if (!IsKeyName(key))
            {
                errors.Add($"'{raw}': キー名 '{key}' に使えない文字がある(英数字と - _ . だけ)");
                continue;
            }
            if (!source.StartsWith('/'))
            {
                errors.Add($"'{raw}': 参照元は / で始まる絶対パスで書く");
                continue;
            }

            var parts = SplitPath(source);
            if (parts is null || parts.Length == 0)
            {
                errors.Add($"'{raw}': 参照元 '{source}' に使えない文字がある(フォルダ名と元のキーは英数字と - _ だけ)");
                continue;
            }
            if (!seen.Add(key))
            {
                errors.Add($"'{raw}': キー '{key}' が重複している(最初の指定を使う)");
                continue;
            }

            var folder = "/" + string.Join('/', parts[..^1]);
            refs.Add(new Reference(key, folder, parts[^1]));
        }
        return (refs, errors);
    }

    /// <summary>
    /// Infisical の参照構文。<c>${prod.shared.entra.client-secret}</c> のように
    /// 環境 slug・フォルダの各部品・キーをドットでつなぐ(ルート直下なら <c>${prod.KEY}</c>)。
    /// サーバ側の解釈は backend/src/services/secret-v2-bridge/secret-reference-fns.ts の getAllSecretReferences。
    /// </summary>
    public static string FormatReference(string envSlug, string sourceFolder, string sourceKey)
    {
        var parts = new List<string> { envSlug };
        parts.AddRange(Matching.Normalize(sourceFolder).Split('/', StringSplitOptions.RemoveEmptyEntries));
        parts.Add(sourceKey);
        return "${" + string.Join('.', parts) + "}";
    }

    /// <summary>
    /// 作るべき (キー, 値) を決める。**フォルダに既にあるキーは決して選ばない**(上書きしない)。
    /// 自分自身を指す参照(循環)も作らない。
    /// </summary>
    public static List<(string Key, string Value)> PlanMissing(
        IEnumerable<Reference> references, IReadOnlySet<string> existingKeys, string envSlug, string folder)
    {
        var plan = new List<(string, string)>();
        var target = Matching.Normalize(folder);
        foreach (var r in references)
        {
            if (existingKeys.Contains(r.Key)) continue;
            if (Matching.Normalize(r.SourceFolder) == target && r.SourceKey == r.Key) continue;
            plan.Add((r.Key, FormatReference(envSlug, r.SourceFolder, r.SourceKey)));
        }
        return plan;
    }
}

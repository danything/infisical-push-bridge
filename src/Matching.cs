namespace InfisicalPushBridge;

/// <summary>
/// イベントの (環境, パス) と InfisicalSecret の secretsScope の突き合わせ。
/// イベント側が null のときは「絞り込めない」なので一致扱い(全 CR リコンサイル)。
/// </summary>
public static class Matching
{
    public static bool Matches(string? eventEnv, string? eventPath, string crEnv, string crPath, bool crRecursive)
    {
        if (eventEnv is not null && !string.Equals(eventEnv, crEnv, StringComparison.Ordinal))
            return false;

        if (eventPath is null) return true;

        var ev = Normalize(eventPath);
        var cr = Normalize(crPath);

        if (ev == cr) return true;

        // recursive な CR は配下のフォルダの変更でも同期対象
        if (crRecursive)
            return cr == "/" || ev.StartsWith(cr + "/", StringComparison.Ordinal);

        return false;
    }

    /// <summary>先頭スラッシュを保証し、末尾スラッシュを落とす("/" だけは残す)。</summary>
    public static string Normalize(string path)
    {
        var p = path.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p;
    }
}

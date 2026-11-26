using System.Text.Json;

namespace DpiBridge.Core;

/// <summary>GitHub Releases の最新リリース情報。</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string ExeUrl, string? Sha256Url, string PageUrl);

public static class UpdateCheck
{
    public const string ExeAssetName = "DpiBridge.exe";
    public const string ShaAssetName = "DpiBridge.exe.sha256";

    /// <summary>"v0.3.0" / "0.3.0" / "v0.3" → Version。読めなければ null。</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (!Version.TryParse(t, out var v)) return null;
        // 0.3 と 0.3.0 を同じものとして比較できるよう 3 桁にそろえる
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    /// <summary>
    /// https://api.github.com/repos/{owner}/{repo}/releases/latest の JSON を解釈する。
    /// 下書き・プレリリース・exe が添付されていないリリースは null。
    /// </summary>
    public static ReleaseInfo? ParseLatestRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;

        string tag = Str(root, "tag_name") ?? "";
        var version = ParseVersion(tag);
        if (version is null) return null;

        string? exe = null, sha = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string? name = Str(a, "name");
                string? url = Str(a, "browser_download_url");
                if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (string.Equals(name, ShaAssetName, StringComparison.OrdinalIgnoreCase)) sha = url;
            }
        }
        if (exe is null) return null;
        return new ReleaseInfo(version, tag, Str(root, "body") ?? "", exe, sha, Str(root, "html_url") ?? "");
    }

    /// <summary>"abcd...  DpiBridge.exe" 形式（sha256sum の出力）またはハッシュのみ → 小文字の16進。</summary>
    public static string? ParseSha256File(string text)
    {
        var token = text.Trim().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        return token.ToLowerInvariant();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

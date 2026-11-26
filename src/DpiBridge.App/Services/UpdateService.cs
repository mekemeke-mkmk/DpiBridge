using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using DpiBridge.Core;

namespace DpiBridge.App.Services;

/// <summary>
/// GitHub Releases から新しいバージョンを確認し、exe を置き換えて再起動する。
/// Windows では実行中の exe の「名前変更」はできるので、
/// 現在の exe → DpiBridge.exe.old に退避 → 新しい exe を置く → 新しい exe を起動 → 自分は終了、の順で行う。
/// </summary>
internal static class UpdateService
{
    public const string UpdatedArg = "--updated";

    public static Version CurrentVersion { get; } = UpdateCheck.Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DpiBridge-Updater", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>新しいリリースがあれば返す。無ければ null。通信エラーは例外。</summary>
    public static async Task<ReleaseInfo?> CheckAsync(string repository, CancellationToken ct = default)
    {
        if (!IsValidRepository(repository)) throw new ArgumentException($"リポジトリ名が不正です: {repository}");
        using var res = await Http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // まだリリースが無い
        res.EnsureSuccessStatusCode();
        var release = UpdateCheck.ParseLatestRelease(await res.Content.ReadAsStringAsync(ct));
        return release is not null && UpdateCheck.IsNewer(release, CurrentVersion) ? release : null;
    }

    public static bool IsValidRepository(string repo)
    {
        var parts = repo.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    /// <summary>ダウンロード・検証・置き換え・再起動。成功したら呼び出し側はアプリを終了すること。</summary>
    public static async Task InstallAsync(ReleaseInfo release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が分かりません");
        string dir = Path.GetDirectoryName(exePath)!;
        string newPath = Path.Combine(dir, "DpiBridge.exe.new");
        string oldPath = exePath + ".old";

        progress?.Report($"{release.Tag} をダウンロードしています...");
        byte[] bytes = await Http.GetByteArrayAsync(release.ExeUrl, ct);
        if (bytes.Length < 50_000 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new InvalidDataException("ダウンロードしたファイルが実行ファイルではありません");

        if (release.Sha256Url is not null)
        {
            progress?.Report("ファイルを検証しています...");
            string? expected = UpdateCheck.ParseSha256File(await Http.GetStringAsync(release.Sha256Url, ct));
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (expected is null || expected != actual)
                throw new InvalidDataException("ダウンロードしたファイルのハッシュが一致しません（壊れているか改ざんの可能性）");
        }

        try
        {
            await File.WriteAllBytesAsync(newPath, bytes, ct);
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(exePath, oldPath);        // 実行中でも名前変更はできる
            try { File.Move(newPath, exePath); }
            catch { File.Move(oldPath, exePath); throw; } // 失敗したら元に戻す
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(newPath);
            throw new UnauthorizedAccessException(
                $"{dir} に書き込めません。DpiBridge を書き込みできるフォルダー（例: ドキュメントやデスクトップ）に置いてください。");
        }

        Log.Info($"更新を適用 {CurrentVersion} → {release.Version}");
        progress?.Report("再起動しています...");
        Process.Start(new ProcessStartInfo(exePath, $"{UpdatedArg} {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>起動時: 前回の更新で残った古いファイルを消す。</summary>
    public static void CleanupOldFiles()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        TryDelete(exe + ".old");
        TryDelete(Path.Combine(Path.GetDirectoryName(exe)!, "DpiBridge.exe.new"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 次回に再挑戦 */ }
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SnapshotWebApi
{
    public static class RepoUtils
    {

private class RepoInfo
{
    public string Archive_url { get; set; } = "";
    public string Default_branch { get; set; } = "";

    public bool Private { get; set; }
    public long Size { get; set; } // in KB
}


        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Downloads and extracts a GitHub repository ZIP snapshot using the GitHub API.
        /// Caller is responsible for cleaning up the returned directory unless 'autoCleanup' is true.
        /// </summary>
        /// <param name="http">HttpClient to use (caller retains ownership)</param>
        /// <param name="owner">GitHub owner/org</param>
        /// <param name="repo">GitHub repository name</param>
        /// <param name="githubToken">Optional GitHub token for higher rate limits</param>
        /// <param name="autoCleanup">If true, temp folder will be deleted on disposal</param>
        /// <returns>Tuple: path to extracted repo root, disposable cleanup handle</returns>
        public static async Task<(string Path, IDisposable Cleanup)> DownloadGitHubRepoSnapshotAsync(
    HttpClient http,
    string owner,
    string repo,
    string? githubToken = null,
    bool autoCleanup = false,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrEmpty(owner)) throw new ArgumentNullException(nameof(owner));
    if (string.IsNullOrEmpty(repo)) throw new ArgumentNullException(nameof(repo));

    var repoApi = $"https://api.github.com/repos/{owner}/{repo}";
    using var infoReq = new HttpRequestMessage(HttpMethod.Get, repoApi);
    infoReq.Headers.UserAgent.ParseAdd("SnapshotWebApi");
    infoReq.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

    if (!string.IsNullOrEmpty(githubToken))
        infoReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

    using var infoRes = await http.SendAsync(infoReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    try
    {
        infoRes.EnsureSuccessStatusCode();
    }
    catch (HttpRequestException ex)
    {
        var baseMessage = $"GitHub API error for {owner}/{repo}. Status: {infoRes.StatusCode}";

        if (infoRes.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingVals) &&
            remainingVals.FirstOrDefault() == "0")
        {
            if (infoRes.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) &&
                long.TryParse(resetVals.FirstOrDefault(), out var resetTime))
            {
                var resetDate = DateTimeOffset.FromUnixTimeSeconds(resetTime).ToLocalTime();
                throw new HttpRequestException($"{baseMessage}. Rate limit exceeded. Resets at {resetDate}.", ex);
            }

            throw new HttpRequestException($"{baseMessage}. Rate limit exceeded.", ex);
        }

        throw new HttpRequestException(baseMessage, ex);
    }

    var info = await infoRes.Content.ReadFromJsonAsync<RepoInfo>(_jsonOpts, cancellationToken)
               ?? throw new JsonException($"Failed to parse GitHub repo info for {owner}/{repo}");

    if (info.Private && string.IsNullOrEmpty(githubToken))
        throw new InvalidOperationException($"A GitHub token is required to access private repository {owner}/{repo}.");

    string branch = info.Default_branch;
    string zipUrl = info.Archive_url.Replace("{archive_format}{/ref}", $"zipball/{branch}");

    using var req = new HttpRequestMessage(HttpMethod.Get, zipUrl);
    req.Headers.UserAgent.ParseAdd("SnapshotWebApi");
    if (!string.IsNullOrEmpty(githubToken))
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

    using var archiveRes = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!archiveRes.IsSuccessStatusCode)
        throw new HttpRequestException($"Failed to download repo snapshot. Status: {archiveRes.StatusCode}");

    string baseTemp = Path.Combine(Path.GetTempPath(), "SnapshotTemp", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(baseTemp);

    try
    {
        await using var stream = await archiveRes.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(baseTemp);

        var dirs = Directory.GetDirectories(baseTemp);
        if (dirs.Length > 1)
            throw new IOException($"Unexpected ZIP structure: multiple top-level directories in {owner}/{repo} archive.");

        string rootDir = dirs.FirstOrDefault() ?? baseTemp;

        if (!Directory.EnumerateFileSystemEntries(rootDir).Any())
            throw new IOException($"Empty repository archive for {owner}/{repo}.");

        return (rootDir, autoCleanup ? new TempDirectoryDisposer(baseTemp) : NoopDisposer.Instance);
    }
    catch (Exception ex)
    {
        try { if (Directory.Exists(baseTemp)) Directory.Delete(baseTemp, true); } catch { }
        throw new IOException($"Failed to extract GitHub archive for {owner}/{repo}", ex);
    }
}


        private sealed class TempDirectoryDisposer : IDisposable
        {
            private readonly string _path;
            private bool _disposed;

            public TempDirectoryDisposer(string path) => _path = path;

            public void Dispose()
            {
                if (_disposed) return;
                try { if (Directory.Exists(_path)) Directory.Delete(_path, true); } catch { }
                _disposed = true;
            }
        }

        private sealed class NoopDisposer : IDisposable
        {
            public static readonly NoopDisposer Instance = new();
            public void Dispose() { }
        }
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace SnapshotWebApi
{
    public static class RepoUtils
    {
        // Only the fields we care about:
        class RepoInfo
        {
            public string Archive_url { get; set; }
            public string Default_branch { get; set; }
        }

        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Downloads & extracts a GitHub repo by reading its own `archive_url` + `default_branch`.
        /// Returns the filesystem path to the repo root.
        /// </summary>
        public static async Task<string> CloneOrDownloadFromGitHubAsync(
            HttpClient http, string owner, string repo)
        {
            // 1) fetch /repos/{owner}/{repo}
            var repoApi = $"https://api.github.com/repos/{owner}/{repo}";
            using var infoReq = new HttpRequestMessage(HttpMethod.Get, repoApi);
            infoReq.Headers.UserAgent.ParseAdd("SnapshotWebApi");
            using var infoRes = await http.SendAsync(infoReq, HttpCompletionOption.ResponseHeadersRead);
            infoRes.EnsureSuccessStatusCode();
            var info = await infoRes.Content.ReadFromJsonAsync<RepoInfo>(_jsonOpts)
                       ?? throw new Exception("Failed to parse repo info");

            // 2) build the ZIP URL
            string branch = info.Default_branch;
            string template = info.Archive_url; 
            // e.g. "https://api.github.com/repos/:owner/:repo/{archive_format}{/ref}"
            var zipUrl = template.Replace("{archive_format}{/ref}", $"zipball/{branch}");

            // 3) try download
            HttpResponseMessage archiveRes = null!;
            foreach (var candidate in new[] { zipUrl, template.Replace("{archive_format}{/ref}", $"tarball/{branch}") })
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                req.Headers.UserAgent.ParseAdd("SnapshotWebApi");
                var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (res.IsSuccessStatusCode)
                {
                    archiveRes = res;
                    break;
                }
                res.Dispose();
            }
            if (archiveRes == null) 
                throw new Exception("Could not download repo archive");

            // 4) extract to a temp dir
            var temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(temp);
            await using (var stream = await archiveRes.Content.ReadAsStreamAsync())
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                zip.ExtractToDirectory(temp);

            // GitHub always wraps content under one top-level folder
            var root = Directory.GetDirectories(temp).FirstOrDefault() ?? temp;
            return root;
        }
    }
}

// Program.cs
using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Snapshot;            // your TLVSnapshot + EmailExport namespace
using SnapshotWebApi;      // the RepoUtils helper

var builder = WebApplication.CreateBuilder(args);

// On Render.com (or Heroku etc) the PORT is injected via env var:
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// register HttpClient for GitHub calls
builder.Services.AddHttpClient("github", c => {
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SnapshotWebApi");
});

var app = builder.Build();

// GET /{owner}/{repo}         → plain TLV
app.MapGet("/{owner}/{repo}", async (string owner, string repo, IHttpClientFactory f) =>
{
    var client = f.CreateClient("github");
    var repoPath = await RepoUtils.CloneOrDownloadFromGitHubAsync(client, owner, repo);

    var outFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tlv");
    TLVSnapshot.Export(repoPath, outFile, ai: false, meta: null);

    return Results.File(outFile, contentType: "text/plain; charset=utf-8");
});

// GET /ai/{owner}/{repo}      → AI-friendly TLV
app.MapGet("/ai/{owner}/{repo}", async (string owner, string repo, IHttpClientFactory f) =>
{
    var client = f.CreateClient("github");
    var repoPath = await RepoUtils.CloneOrDownloadFromGitHubAsync(client, owner, repo);

    var outFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.ai.tlv");
    TLVSnapshot.Export(repoPath, outFile, ai: true, meta: null);

    return Results.File(outFile, contentType: "text/plain; charset=utf-8");
});

// GET /b64/{owner}/{repo}     → Base64 email TLV
app.MapGet("/b64/{owner}/{repo}", async (string owner, string repo, IHttpClientFactory f) =>
{
    var client = f.CreateClient("github");
    var repoPath = await RepoUtils.CloneOrDownloadFromGitHubAsync(client, owner, repo);

    // your EmailExport from the CLI code
    var text = EmailExport.ExportAsBase64Email(repoPath);
    return Results.Text(text, contentType: "text/plain; charset=utf-8");
});

app.Run();

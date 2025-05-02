// Program.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snapshot;
using SnapshotWebApi;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static SnapshotWebApi.RepoUtils;

var builder = WebApplication.CreateBuilder(args);

// Optional for Render/Heroku-style hosts:
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// GitHub configuration
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

// Configure HttpClient with appropriate timeout for large repos
builder.Services.AddHttpClient("github", c => {
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SnapshotWebApi/1.0");
    c.Timeout = TimeSpan.FromMinutes(5); // Adjust timeout for large repos
});

// Add logging
builder.Services.AddLogging();
builder.Services.AddCors();

var app = builder.Build();

// Add middleware for request logging
app.Use(async (context, next) => {
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Request: {Method} {Path}", context.Request.Method, context.Request.Path);
    
    var startTime = DateTime.UtcNow;
    await next();
    var elapsed = DateTime.UtcNow - startTime;
    
    logger.LogInformation("Response: {StatusCode} in {ElapsedMs}ms", 
        context.Response.StatusCode, elapsed.TotalMilliseconds);
});

app.UseCors(policy => 
    policy.AllowAnyOrigin()
          .AllowAnyMethod()
          .AllowAnyHeader());

// Minimal routing with flags
app.MapGet("/ai/{owner}/{repo}", ExportHandler(ai: true, b64: false));
app.MapGet("/b64/{owner}/{repo}", ExportHandler(ai: false, b64: true));
app.MapGet("/{owner}/{repo}", ExportHandler(ai: false, b64: false));

// HEAD request for CDN preflight or bots
app.MapMethods("/{owner}/{repo}", new[] { "HEAD" }, ctx =>
{
    ctx.Response.StatusCode = 204; // No Content
    return Task.CompletedTask;
});

app.Run();

static RequestDelegate ExportHandler(bool ai, bool b64) =>
    async (HttpContext ctx) =>
    {
        var owner = (string)ctx.Request.RouteValues["owner"]!;
        var repo = (string)ctx.Request.RouteValues["repo"]!;
        
        // Basic validation
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await ctx.Response.WriteAsync("Invalid owner or repository name");
            return;
        }
        
        var factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("github");
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        
        // Get GitHub token from environment or request
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(githubToken) && ctx.Request.Headers.TryGetValue("X-GitHub-Token", out var tokenHeader))
        {
            githubToken = tokenHeader.ToString();
        }
        
        // Setup cancellation - linked to request cancellation
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(10)); // Safety timeout
        
        string? tempOut = null;
        
        try
        {
            logger.LogInformation("Downloading repo {Owner}/{Repo}", owner, repo);
            var (repoPath, cleanup) = await DownloadGitHubRepoSnapshotAsync(
                client, 
                owner, 
                repo, 
                githubToken: githubToken, 
                autoCleanup: true,
                cancellationToken: cts.Token);
                
            // Use the cleanup in a using statement to ensure it's disposed
            using (cleanup)
            {
                logger.LogInformation("Successfully downloaded repo {Owner}/{Repo} to {Path}", owner, repo, repoPath);
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                
                if (b64)
                {
                    logger.LogInformation("Exporting as Base64 email");
                    var text = EmailExport.ExportAsBase64Email(repoPath);
                    await ctx.Response.WriteAsync(text, cts.Token);
                }
                else
                {
                    logger.LogInformation("Exporting as TLV file (AI: {AI})", ai);
                    tempOut = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tlv");
                    
                    // Build metadata for snapshot
                    var meta = new Dictionary<string, string>
                    {
                        ["source"] = $"github:{owner}/{repo}",
                        ["exported_at"] = DateTime.UtcNow.ToString("o"),
                        ["export_type"] = ai ? "ai" : "standard"
                    };
                    
                    TLVSnapshot.Export(repoPath, tempOut, ai, meta);
                    await ctx.Response.SendFileAsync(tempOut, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Request for {Owner}/{Repo} was cancelled", owner, repo);
            ctx.Response.StatusCode = 499; // Client Closed Request
            await ctx.Response.WriteAsync("Operation was cancelled", cts.Token);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP error while processing {Owner}/{Repo}", owner, repo);
            
            // Handle rate limiting specifically
            if (ex.Message.Contains("Rate limit exceeded"))
            {
                ctx.Response.StatusCode = 429; // Too Many Requests
                await ctx.Response.WriteAsync($"GitHub API rate limit exceeded. {ex.Message}");
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsync($"GitHub API error: {ex.Message}");
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("private repository"))
        {
            logger.LogError(ex, "Attempted to access private repo {Owner}/{Repo} without token", owner, repo);
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await ctx.Response.WriteAsync("GitHub authentication required for private repositories");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing {Owner}/{Repo}", owner, repo);
            ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await ctx.Response.WriteAsync($"Error: {ex.Message}");
        }
        finally
        {
            // Clean up temp file
            if (tempOut != null && File.Exists(tempOut))
            {
                try
                {
                    File.Delete(tempOut);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete temporary file: {Path}", tempOut);
                }
            }
        }
    };

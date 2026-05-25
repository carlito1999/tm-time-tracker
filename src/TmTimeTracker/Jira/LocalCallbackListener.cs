using System.Net;

namespace TmTimeTracker.Jira;

public sealed class LocalCallbackListener
{
    public sealed record CallbackResult(string Code, string State);

    public async Task<CallbackResult> ListenOnceAsync(string prefix, CancellationToken ct)
    {
        if (!prefix.EndsWith("/")) prefix += "/";

        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();

        var ctxTask = http.GetContextAsync();
        using var reg = ct.Register(() => http.Stop());
        var ctx = await ctxTask.ConfigureAwait(false);

        var query = ctx.Request.QueryString;
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];

        string body;
        if (!string.IsNullOrEmpty(error))
        {
            ctx.Response.StatusCode = 400;
            body = $"<html><body><h1>Authorization failed</h1><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
        }
        else
        {
            ctx.Response.StatusCode = 200;
            body = "<html><body><h2>TmTimeTracker connected. You can close this tab.</h2></body></html>";
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();

        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException($"OAuth callback returned no code (error={error}).");
        return new CallbackResult(code!, state ?? "");
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace Consent.Wpf.Auth;

/// <summary>
/// Opens the authorize/end-session URL in the user's default browser and waits for
/// Keycloak to redirect back to the loopback address. A raw <see cref="TcpListener"/>
/// is used instead of <see cref="HttpListener"/> so no URL ACL / admin rights are needed.
/// </summary>
public sealed class SystemBrowser(int port) : IBrowser
{
    public int Port { get; } = port;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        try
        {
            Process.Start(new ProcessStartInfo(options.StartUrl) { UseShellExecute = true });

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            // Browsers may open extra connections (favicon, prefetch); loop until we see the redirect.
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(linked.Token);
                await using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, linked.Token);

                if (requestLine is null || !TryGetPath(requestLine, out var pathAndQuery))
                {
                    continue;
                }

                var callback = new Uri(new Uri(options.EndUrl), pathAndQuery);
                if (callback.AbsolutePath.TrimEnd('/') != new Uri(options.EndUrl).AbsolutePath.TrimEnd('/'))
                {
                    await WriteResponseAsync(stream, "404 Not Found", "<h1>Not found</h1>", linked.Token);
                    continue;
                }

                await WriteResponseAsync(stream, "200 OK", SuccessPage, linked.Token);
                return new BrowserResult { ResultType = BrowserResultType.Success, Response = callback.ToString() };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BrowserResult { ResultType = BrowserResultType.UserCancel };
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Timed out waiting for the browser." };
        }
        catch (Exception ex)
        {
            return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, ct);
        if (read == 0)
        {
            return null;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static bool TryGetPath(string requestLine, out string pathAndQuery)
    {
        // "GET /callback?code=...&state=... HTTP/1.1"
        var parts = requestLine.Split(' ');
        pathAndQuery = parts.Length >= 2 ? parts[1] : string.Empty;
        return parts.Length >= 2 && parts[0] == "GET";
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private const string SuccessPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Consent Demo</title>
        <style>body{font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#f4f6f8;color:#1f2933}
        .card{background:#fff;padding:2rem 3rem;border-radius:12px;box-shadow:0 4px 24px rgba(0,0,0,.08);text-align:center}</style></head>
        <body><div class="card"><h2>Done</h2><p>You can close this tab and return to the Consent Demo app.</p></div></body></html>
        """;
}

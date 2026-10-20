using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Rakit.Core.Workflows;

/// <summary>Bound from <c>Rakit:Workflow</c>. There is deliberately no code default for the list (configuration arrays append to non-empty defaults).</summary>
public sealed class WorkflowOptions
{
    public const string SectionName = "Rakit:Workflow";

    /// <summary>
    /// Hosts a Rest step may call: <c>api.example.com</c> (any port) or <c>localhost:5099</c> (that port only). Empty = no Rest
    /// step can run. A call to any other host is refused, so a builder cannot use this server to reach internal addresses.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Limits of the Javascript step's sandbox (<c>Rakit:Workflow:Script</c>).</summary>
    public ScriptOptions Script { get; set; } = new();

    /// <summary>Whether <paramref name="url"/> may be called according to <see cref="AllowedHosts"/>.</summary>
    public bool IsAllowed(Uri url)
    {
        foreach (var entry in AllowedHosts)
        {
            var text = entry?.Trim() ?? "";
            if (text.Length == 0) continue;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
            {
                if (string.Equals(url.Host, text[..colon], StringComparison.OrdinalIgnoreCase) && url.Port == port) return true;
            }
            else if (string.Equals(url.Host, text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>The sandbox of the Javascript step. Values outside the ranges are clamped to them.</summary>
public sealed class ScriptOptions
{
    public const int MaxTimeoutMs = 5_000;
    public const int MaxMemoryMb = 128;

    /// <summary>Wall-clock time one run may take.</summary>
    public int TimeoutMs { get; set; } = 500;

    public int MemoryLimitMb { get; set; } = 16;
    public int MaxStatements { get; set; } = 100_000;
    public int MaxRecursion { get; set; } = 100;

    /// <summary>Largest JSON a script may return.</summary>
    public int MaxResultBytes { get; set; } = 65_536;
}

/// <summary>Bound from <c>Rakit:Mail</c>. Without <see cref="Host"/> and <see cref="From"/> an Email step is only logged, not sent.</summary>
public sealed class MailOptions
{
    public const string SectionName = "Rakit:Mail";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;

    /// <summary>A MailKit <c>SecureSocketOptions</c> name: None, Auto, SslOnConnect, StartTls, StartTlsWhenAvailable.</summary>
    public string Security { get; set; } = "StartTlsWhenAvailable";

    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Rakit";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

// ---- Rest -----------------------------------------------------------------------------------------

public sealed record RestRequest(
    string Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body, string? ContentType,
    int TimeoutSeconds, int Retries);

/// <param name="Body">At most <see cref="HttpRestCaller.MaxBodyBytes"/> bytes of the answer, as text.</param>
public sealed record RestResponse(int Status, string Body, bool BodyTruncated);

/// <summary>The host is not in <c>Rakit:Workflow:AllowedHosts</c>, or the scheme is not http/https.</summary>
public sealed class RestHostNotAllowedException(Uri url) : Exception($"Host '{url.Authority}' is not allowed for Rest steps.");

/// <summary>The call failed after its retries (network error, timeout). The message is safe for end users; details are logged.</summary>
public sealed class RestCallException(string message, bool timeout, Exception? inner = null) : Exception(message, inner)
{
    public bool IsTimeout { get; } = timeout;
}

public interface IRestCaller
{
    /// <exception cref="RestHostNotAllowedException"/>
    /// <exception cref="RestCallException"/>
    Task<RestResponse> SendAsync(RestRequest request, CancellationToken ct = default);
}

public sealed class HttpRestCaller(
    IHttpClientFactory clients, IOptions<WorkflowOptions> options, ILogger<HttpRestCaller> logger) : IRestCaller
{
    public const string ClientName = "rakit-workflow";
    public const int MaxBodyBytes = 65_536;

    // set by the transport (or dangerous to let a builder set)
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "TE", "Trailer", "Proxy-Authorization",
    };

    public async Task<RestResponse> SendAsync(RestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Url.Scheme is not ("http" or "https") || !options.Value.IsAllowed(request.Url))
            throw new RestHostNotAllowedException(request.Url);

        var client = clients.CreateClient(ClientName);
        var attempts = Math.Clamp(request.Retries, 0, RestStepConfig.MaxRetries) + 1;

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, RestStepConfig.MaxTimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                using var message = Build(request);
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                var status = (int)response.StatusCode;
                if (attempt < attempts && IsRetryable(status))
                {
                    await Task.Delay(Delay(attempt), ct);
                    continue;
                }
                var (body, truncated) = await ReadBodyAsync(response, linked.Token);
                return new RestResponse(status, body, truncated);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                var timedOut = timeout.IsCancellationRequested;
                logger.LogWarning(ex, "Rest step call to {Host} failed (attempt {Attempt}/{Attempts}, {Kind}).",
                    request.Url.Authority, attempt, attempts, timedOut ? "timeout" : "network");
                if (attempt >= attempts)
                    throw new RestCallException(timedOut ? "Layanan eksternal tidak menjawab tepat waktu." : "Layanan eksternal tidak dapat dihubungi.", timedOut, ex);
                await Task.Delay(Delay(attempt), ct);
            }
        }
    }

    private static bool IsRetryable(int status) => status is 408 or 429 or >= 500;
    private static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));

    private static HttpRequestMessage Build(RestRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        foreach (var (name, value) in request.Headers)
        {
            if (BlockedHeaders.Contains(name) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrEmpty(request.Body) && request.Method != "GET")
        {
            var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType.Trim();
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsed)
                ? new MediaTypeHeaderValue(parsed.MediaType ?? "application/json") { CharSet = "utf-8" }
                : new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        return message;
    }

    private static async Task<(string Body, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        var truncated = read > MaxBodyBytes;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxBodyBytes)), truncated);
    }
}

// ---- Email ----------------------------------------------------------------------------------------

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string Body, bool IsHtml);

public interface IWorkflowEmailSender
{
    /// <summary>False when <c>Rakit:Mail</c> has no host or sender: nothing can be sent.</summary>
    bool IsConfigured { get; }

    /// <exception cref="InvalidOperationException">Not configured.</exception>
    /// <exception cref="Exception">The SMTP server refused or could not be reached (details are for the log).</exception>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class MailKitEmailSender(IOptions<MailOptions> options) : IWorkflowEmailSender
{
    private const int TimeoutMilliseconds = 30_000;

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.IsConfigured) throw new InvalidOperationException("SMTP is not configured (Rakit:Mail).");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.From));
        foreach (var to in message.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;
        mime.Body = new TextPart(message.IsHtml ? "html" : "plain") { Text = message.Body };

        var security = Enum.TryParse<SecureSocketOptions>(settings.Security, ignoreCase: true, out var parsed)
            ? parsed : SecureSocketOptions.StartTlsWhenAvailable;

        using var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        await client.ConnectAsync(settings.Host, settings.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(settings.User)) await client.AuthenticateAsync(settings.User, settings.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}

// ---- Module links -------------------------------------------------------------------------------

public interface IModuleLinkResolver
{
    /// <summary>The runtime path of a published module (<c>/object/page/{id}</c>), or null when it is gone or still a draft.</summary>
    Task<string?> PathOfAsync(Guid moduleId, CancellationToken ct = default);
}

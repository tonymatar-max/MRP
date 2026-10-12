using System.Net.Sockets;
using System.Security.Authentication;

namespace CobaltMrp.Api.Services;

/// <summary>Turns a failed Service Layer call into one plain sentence a person can act on (no stack traces, no HTML pages).</summary>
public static class ServiceLayerDiagnostics
{
    /// <summary>Why the base URL cannot be used, or null when it looks fine. Expects an already trimmed value.</summary>
    public static string? ValidateBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "Enter the base URL first.";
        if (baseUrl.Any(char.IsWhiteSpace)) return "The base URL contains spaces. Use something like https://server:50000/b1s/v1.";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "The base URL is not a valid web address. Use something like https://server:50000/b1s/v1.";
        return null;
    }

    public static string Explain(Exception ex, string? baseUrl, bool allowInvalidCertificates)
    {
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException) chain.Add(e);
        var root = chain[^1];
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri);
        var where = uri is null ? "the server" : $"{uri.Host}:{uri.Port}";

        if (chain.Any(e => e is AuthenticationException) || chain.Any(e => e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)))
        {
            return allowInvalidCertificates
                ? $"The secure (TLS) connection to {where} failed: {root.Message}"
                : $"The server's certificate was not accepted ({root.Message.TrimEnd('.')}). If it is a self-signed certificate, turn on 'Allow invalid certificates' in Settings > Service Layer.";
        }
        if (chain.OfType<SocketException>().FirstOrDefault() is { } sock)
            return sock.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"The server name '{uri?.Host}' could not be found. Check the base URL.",
                SocketError.ConnectionRefused => $"Nothing is listening on {where} (connection refused). Check the port and that the Service Layer is running.",
                SocketError.TimedOut => $"{where} did not answer (timed out). Check the address, the port and any firewall.",
                _ => $"Could not connect to {where}: {sock.Message}",
            };
        if (chain.Any(e => e is TaskCanceledException or TimeoutException))
            return $"{where} did not answer in time. Check the address, the port and any firewall.";
        if (uri?.Scheme == Uri.UriSchemeHttp && chain.Any(e => e is HttpRequestException or System.IO.IOException))
            return $"{where} did not answer as a web server over plain http ({root.Message.TrimEnd('.')}). The Service Layer normally needs https: try https://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}.";
        return ex.Message.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase) && root != ex
            ? $"Could not talk to {where}: {root.Message}"
            : ex.Message;
    }
}

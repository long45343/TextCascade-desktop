using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace TextCascadeSharp.Core;

public sealed class ClipApiClient
{
    private static readonly Regex LoginCsrfInputRegex = new("<input\\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRegex = new("\\bname\\s*=\\s*(['\"])_csrf\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValueRegex = new("\\bvalue\\s*=\\s*(['\"])(.*?)\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<LoginResult> LoginAsync(
        string serverUrl,
        string username,
        string passwordSha3,
        string hashedPasswordBase64,
        CancellationToken cancellationToken)
    {
        var normalizedServerUrl = SettingsStore.NormalizeServerUrl(serverUrl);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using var client = CreateClient(handler);

        var loginPage = await client.GetAsync(normalizedServerUrl + "/login", cancellationToken).ConfigureAwait(false);
        var loginHtml = await loginPage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(loginPage, UiText.FetchLoginPageFailed);

        var csrf = FindLoginCsrf(loginHtml);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            throw new InvalidOperationException(UiText.CsrfTokenNotFound);
        }

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", passwordSha3),
            new KeyValuePair<string, string>("_csrf", csrf)
        });
        var loginResponse = await client.PostAsync(normalizedServerUrl + "/login", form, cancellationToken).ConfigureAwait(false);
        var loginBody = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!loginResponse.IsSuccessStatusCode || loginBody.Contains("bad credentials", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(UiText.LoginRejectedStatus((int)loginResponse.StatusCode));
        }

        var cookieHeader = BuildCookieHeader(handler.CookieContainer, new Uri(normalizedServerUrl));
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new InvalidOperationException(UiText.NoAuthenticatedSessionCookie);
        }

        var serverModeJson = await GetJsonAsync(client, normalizedServerUrl + "/server-mode", "server mode", cancellationToken).ConfigureAwait(false);
        var serverMode = JsonUtil.StringField(serverModeJson, "mode", "P2S");
        if (!serverMode.Equals("P2S", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(UiText.P2SOnly(serverMode));
        }

        var maxSizeJson = await GetJsonAsync(client, normalizedServerUrl + "/max-size", "max-size", cancellationToken).ConfigureAwait(false);
        var maxSize = JsonUtil.LongField(maxSizeJson, "maxsize", ClipConfig.DefaultMaxSizeBytes);

        var csrfToken = string.Empty;
        try
        {
            var csrfJson = await GetJsonAsync(client, normalizedServerUrl + "/csrf-token", "csrf-token", cancellationToken).ConfigureAwait(false);
            csrfToken = JsonUtil.StringField(csrfJson, "token", "");
        }
        catch
        {
            csrfToken = string.Empty;
        }

        return new LoginResult(
            normalizedServerUrl,
            ClipConfig.WebsocketUrlFromServerUrl(normalizedServerUrl),
            passwordSha3,
            hashedPasswordBase64,
            csrfToken,
            cookieHeader,
            maxSize);
    }

    public async Task LogoutAsync(string serverUrl, string cookieHeader, string csrfToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return;
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = CreateClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, SettingsStore.NormalizeServerUrl(serverUrl) + "/logout");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("_csrf", csrfToken) });
        using var _ = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    private static async Task<string> GetJsonAsync(HttpClient client, string url, string name, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, UiText.RequestFailedAfterLogin(name));
        if (!body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(UiText.JsonExpectedAfterLogin(name));
        }
        return body;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string prefix)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(UiText.RequestFailed(prefix, (int)response.StatusCode));
        }
    }

    private static string FindLoginCsrf(string html)
    {
        foreach (Match input in LoginCsrfInputRegex.Matches(html))
        {
            if (!NameRegex.IsMatch(input.Value))
            {
                continue;
            }
            var value = ValueRegex.Match(input.Value);
            if (value.Success)
            {
                return WebUtility.HtmlDecode(value.Groups[2].Value);
            }
        }
        return string.Empty;
    }

    private static string BuildCookieHeader(CookieContainer container, Uri serverUri)
    {
        var builder = new StringBuilder();
        foreach (Cookie cookie in container.GetCookies(serverUri))
        {
            if (string.IsNullOrWhiteSpace(cookie.Name))
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }
            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }
        return builder.ToString();
    }
}

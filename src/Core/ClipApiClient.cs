using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace TextCascadeSharp.Core;

// ClipCascade 服务端 HTTP API 客户端。负责登录、注销、获取服务器配置。
// WebSocket 握手需要 JSESSIONID cookie，所以登录阶段必须保留 CookieContainer。
public sealed class ClipApiClient
{
    // ClipCascade 服务端使用 Thymeleaf + Spring Security。
    // login.html 模板里写 <form th:action="@{/login}">，渲染时
    // Spring Security CsrfFilter 会自动插入一个 hidden input：
    //   <input type="hidden" name="_csrf" value="<uuid>" />
    // 本客户端用正则提取这个 input 的 value。
    // 参考 Python 端用 BeautifulSoup 解析同样的标记，本端保持依赖无关
    // （review issue #11）。支持单引号或双引号属性。
    private static readonly Regex LoginCsrfInputRegex = new("<input\\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRegex = new("\\bname\\s*=\\s*(['\"])_csrf\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValueRegex = new("\\bvalue\\s*=\\s*(['\"])(.*?)\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 登录流程：
    //   1) GET /login 拿到登录页 HTML，提取 CSRF token
    //   2) POST /login 提交表单（用户名 + 密码 SHA3 + CSRF）
    //   3) 从 CookieContainer 提取 JSESSIONID
    //   4) GET /server-mode 确认服务端模式为 P2S（Peer to Server）
    //   5) GET /max-size 拿到服务端允许的最大内容字节数
    //   6) GET /csrf-token 拿到后续 /logout 用的 CSRF（失败可忽略）
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
            AllowAutoRedirect = true,           // 登录成功会被 302 重定向到 /
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using var client = CreateClient(handler);

        // 1) 获取登录页 + CSRF
        var loginPage = await client.GetAsync(normalizedServerUrl + "/login", cancellationToken).ConfigureAwait(false);
        var loginHtml = await loginPage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(loginPage, UiText.FetchLoginPageFailed);

        var csrf = FindLoginCsrf(loginHtml);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            throw new InvalidOperationException(UiText.CsrfTokenNotFound);
        }

        // 2) 提交登录表单。密码字段传 SHA3-512 hex，服务端会用相同算法验证
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", passwordSha3),
            new KeyValuePair<string, string>("_csrf", csrf)
        });
        var loginResponse = await client.PostAsync(normalizedServerUrl + "/login", form, cancellationToken).ConfigureAwait(false);
        var loginBody = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // 服务端登录失败时返回的 body 包含 "bad credentials" 字符串
        // （Spring Security 默认 BadCredentialsException 的提示）。
        // 这是脆弱的启发式（review S4），但服务端未提供结构化错误响应前只能如此。
        if (!loginResponse.IsSuccessStatusCode || loginBody.Contains("bad credentials", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(UiText.LoginRejectedStatus((int)loginResponse.StatusCode));
        }

        // 3) 提取会话 Cookie。WebSocket 握手时必须带上 JSESSIONID
        var cookieHeader = BuildCookieHeader(handler.CookieContainer, new Uri(normalizedServerUrl));
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new InvalidOperationException(UiText.NoAuthenticatedSessionCookie);
        }

        // 4) 确认服务端模式为 P2S。本客户端只支持 P2S 模式
        var serverModeJson = await GetJsonAsync(client, normalizedServerUrl + "/server-mode", "server mode", cancellationToken).ConfigureAwait(false);
        var serverMode = JsonUtil.StringField(serverModeJson, "mode", "P2S");
        if (!serverMode.Equals("P2S", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(UiText.P2SOnly(serverMode));
        }

        // 5) 获取服务端允许的最大内容字节数
        var maxSizeJson = await GetJsonAsync(client, normalizedServerUrl + "/max-size", "max-size", cancellationToken).ConfigureAwait(false);
        var maxSize = JsonUtil.LongField(maxSizeJson, "maxsize", ClipConfig.DefaultMaxSizeBytes);

        // 6) 获取 /logout 用的 CSRF token。失败不影响登录，可忽略
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

    // 注销：向 /logout POST 表单，包含登录时拿到的 CSRF token
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

    // 创建短生命周期的 HttpClient。登录/注销都是用户手动触发的低频操作，
    // 每次 new 不会造成 socket 耗尽（review issue #12 已评估保留）
    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    // GET 一个 URL 并返回 body 字符串，要求 body 是 JSON（以 { 开头）
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

    // 从登录页 HTML 中提取 CSRF token。
    // 服务端模板固定为 <input type="hidden" name="_csrf" value="...">
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

    // 把 CookieContainer 中的 cookie 拼成 "name1=value1; name2=value2" 格式
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

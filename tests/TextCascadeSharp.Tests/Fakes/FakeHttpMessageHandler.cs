using System.Net;

namespace TextCascadeSharp.Tests.Fakes;

// 队列驱动的假 HttpMessageHandler，供 ClipApiClient 登录/注销流程测试。
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public int RequestCount { get; private set; }

    public void Enqueue(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (_responses.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        return Task.FromResult(_responses.Dequeue());
    }

    internal static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        };
    }

    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    internal static HttpResponseMessage LoginSuccess(string cookie = "JSESSIONID=abc123")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("")
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", cookie + "; Path=/; HttpOnly");
        return response;
    }
}

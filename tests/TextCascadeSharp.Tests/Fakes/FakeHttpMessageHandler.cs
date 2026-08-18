using System.Net;

namespace TextCascadeSharp.Tests.Fakes;

// 队列驱动的假 HttpMessageHandler，供 ClipApiClient 登录流程测试。
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    // 每次请求回调（记录请求内容做断言；可异步读取请求体）
    public Func<HttpRequestMessage, Task>? OnRequest { get; set; }

    public int RequestCount { get; private set; }

    public void Enqueue(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (OnRequest is { } callback)
        {
            await callback(request).ConfigureAwait(false);
        }
        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        return _responses.Dequeue();
    }

    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

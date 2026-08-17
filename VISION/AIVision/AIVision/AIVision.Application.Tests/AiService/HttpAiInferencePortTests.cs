using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.AiService;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIVision.Application.Tests.AiService;

public class HttpAiInferencePortTests
{
    [Fact]
    public async Task Should_Send_Request_And_Return_Prediction()
    {
        HttpRequestMessage? capturedRequest = null;

        var handler = new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            var body = await request.Content!.ReadAsStringAsync();
            var json = JsonDocument.Parse(body);

            Assert.True(json.RootElement.TryGetProperty("imageBase64", out var imageBase64));
            Assert.Equal("Mono8", json.RootElement.GetProperty("pixelFormat").GetString());

            var responsePayload = new
            {
                label = "OK",
                confidence = 0.98f,
                isOk = true,
                modelVersion = "pcb-v1.2",
                imagePath = "s3://bucket/annotated.png",
                detections = new[]
                {
                    new
                    {
                        label = "defect",
                        confidence = 0.42f,
                        box = new { x = 10f, y = 20f, width = 30f, height = 40f }
                    }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(responsePayload), Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ai.test") };

        var options = Options.Create(new AiServiceOptions
        {
            Type = "Http",
            BaseUrl = "https://ai.test",
            PredictPath = "/v1/inference",
            ApiKey = "demo",
            ApiKeyPrefix = "Bearer ",
            ApiKeyHeader = "Authorization",
            TimeoutMs = 2000,
            RetryCount = 0,
            Model = "pcb-v1"
        });
        var port = new HttpAiInferencePort(client, options, NullLogger<HttpAiInferencePort>.Instance);

        var image = new ImageData(new byte[] { 1, 2, 3 }, 10, 10, "Mono8");
        var prediction = await port.PredictAsync(image, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.True(capturedRequest.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Contains("Bearer demo", authValues);

        Assert.Equal("OK", prediction.Label);
        Assert.True(prediction.IsOk);
        Assert.Single(prediction.Detections);
        Assert.Equal(30f, prediction.Detections[0].BoundingBox.Width);
    }

    [Fact]
    public async Task Should_Throw_When_Service_Returns_Error()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid\"}", Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ai.test") };
        var options = Options.Create(new AiServiceOptions
        {
            Type = "Http",
            BaseUrl = "https://ai.test",
            PredictPath = "/v1/inference",
            TimeoutMs = 2000,
            RetryCount = 0
        });
        var port = new HttpAiInferencePort(client, options, NullLogger<HttpAiInferencePort>.Instance);

        var image = new ImageData(new byte[] { 1, 2, 3 }, 10, 10, "Mono8");

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.PredictAsync(image, CancellationToken.None));
    }

    [Fact]
    public async Task Should_Throw_When_Http_Mode_Disabled()
    {
        var port = new HttpAiInferencePort(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new AiServiceOptions { Type = "Fake" }),
            NullLogger<HttpAiInferencePort>.Instance);

        var image = new ImageData(Array.Empty<byte>(), 1, 1, "Mono8");

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.PredictAsync(image, CancellationToken.None));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = request => Task.FromResult(handler(request));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}

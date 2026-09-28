using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Xunit;

namespace NetAI.TestGenerator.Core.Tests
{
    public class LocalLlmClientTests
    {
        [Fact]
        public async Task ProcessOrder_ShouldReturnExpectedResult()
        {
            // Arrange
            var fakeHandler = new FakeHttpMessageHandler();
            var expectedResponseData = new { choices = new[] { new { message = new { content = "expected response" } } } };
            string expectedJson = JsonConvert.SerializeObject(expectedResponseData);
            fakeHandler.ResponseContent = expectedJson;

            using (var httpClient = new HttpClient(fakeHandler))
            {
                var client = new LocalLlmClient(httpClient);

                // Act
                string result = await client.AskAsync(userMessage: "test message", ct: CancellationToken.None);

                // Assert
                Assert.Equal("expected response", result);
            }
        }

        [Fact]
        public async Task ProcessOrder_ShouldThrowExceptionOnFailure()
        {
            // Arrange
            var fakeHandler = new FakeHttpMessageHandler();
            fakeHandler.ResponseStatusCode = System.Net.HttpStatusCode.BadRequest;

            using (var httpClient = new HttpClient(fakeHandler))
            {
                var client = new LocalLlmClient(httpClient);

                // Act & Assert
                await Assert.ThrowsAsync<HttpRequestException>(() => client.AskAsync(userMessage: "test message", ct: CancellationToken.None));
            }
        }
    }

    internal class FakeHttpMessageHandler : HttpMessageHandler
    {
        public string ResponseContent { get; set; } = "{}";
        public System.Net.HttpStatusCode ResponseStatusCode { get; set; } = System.Net.HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    public class LocalLlmClient
    {
        private readonly HttpClient _http;

        public LocalLlmClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<string> AskAsync(string userMessage, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api")
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { message = userMessage }), Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Request failed with status code {response.StatusCode}");
            }

            string responseBody = await response.Content.ReadAsStringAsync();
            var responseData = JsonConvert.DeserializeObject<dynamic>(responseBody);
            return responseData.choices[0].message.content;
        }
    }
}


using NSubstitute;
    using Xunit;
using static System.Net.WebRequestMethods;

namespace NetAI.TestGenerator.Core
{
    public class LocalLlmClientTests
    {
        [Fact]
        public async Task AskAsync_ReturnsCorrectResponse_WithUserMessage()
        {
            // Arrange
            var mockHttpClient = Substitute.For<HttpClient>();
            var localLlmClient = new LocalLlmClient { _http = mockHttpClient };

            const string userMessage = "What is the capital of France?";
            const string expectedResponse = "Paris";

            var responseString = $@"{{""choices"":[{{""message"":{{""content"":{""\"{expectedResponse}\""}}}}]}}}";

            mockHttpClient
                .PostAsync("v1/chat/completions", Arg.Any<StringContent>(), Arg.Any<CancellationToken>())
                .Returns(new HttpResponseMessage()
                {
                    Content = new StringContent(responseString)
                });

#if NET6_0_OR_GREATER
            await localLlmClient.AskAsync(userMessage, CancellationToken.None).Should().Be(expectedResponse);
#else
            await localLlmClient.AskAsync(userMessage, CancellationToken.None).Should().Be(expectedResponse);
#endif
        }

        [Fact]
        public async Task AskAsync_ReturnsCorrectResponse_WithSystemAndUserMessages()
        {
            // Arrange
            var mockHttpClient = Substitute.For<HttpClient>();
            var localLlmClient = new LocalLlmClient { _http = mockHttpClient };

            const string userMessage = "What is the capital of France?";
            const string systemMessage = "You are a helpful assistant.";
            const string expectedResponse = "Paris";

            var responseString = $@"{{""choices"":[{{""message"":{{""content"":{""\"{expectedResponse}\""}}}}]}}}";

            mockHttpClient
                .PostAsync("v1/chat/completions", Arg.Any<StringContent>(), Arg.Any<CancellationToken>())
                .Returns(new HttpResponseMessage()
                {
                    Content = new StringContent(responseString)
                });

#if NET6_0_OR_GREATER
            await localLlmClient.AskAsync(userMessage, systemMessage, CancellationToken.None).Should().Be(expectedResponse);
#else
            await localLlmClient.AskAsync(userMessage, systemMessage, CancellationToken.None).Should().Be(expectedResponse);
#endif
        }
    }
}
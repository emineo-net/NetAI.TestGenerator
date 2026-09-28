using Xunit;
namespace NetAI.Generated.Tests
{
    public class MainWindowTests
    {
        [Fact]
        public void OnLoaded_ShouldResolveAndSetExtractorHostContent()
        {
            // Arrange
            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockTextExtractorUserControl = new Mock<TextExtractorUserControl>();
            mockServiceProvider.Setup(sp => sp.GetRequiredService<TextExtractorUserControl>()).Returns(mockTextExtractorUserControl.Object);

            var mainWindow = new MainWindow
            {
                ServiceProvider = mockServiceProvider.Object
            };

            // Act
            mainWindow.OnLoaded(null, null);

            // Assert
            Assert.Equal(mockTextExtractorUserControl.Object, mainWindow.ExtractorHost.Content);
        }
    }
}
using WpftranlationTestApp;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;
namespace NetAI.Generated.Tests
{
    public class MainWindowTests
    {
        [Fact]
        public void IsValidWebUrl_ValidHttpUrl_ReturnsTrue()
        {
            // Arrange
            string url = "http://example.com";

            // Act
            bool result = MainWindow.IsValidWebUrl(url);

            // Assert
            Assert.True(result);
        }
    }
}
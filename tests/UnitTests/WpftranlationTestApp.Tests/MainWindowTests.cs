using FluentAssertions;
using Moq;
using System.Threading.Tasks;
using System;
using Xunit;

namespace WpftranlationTestApp.Tests
{
    public class MainWindowTests
    {
        private readonly MainWindow _sut; public MainWindowTests()
        {
            _sut = new MainWindow();
        }
        [StaFact]
        public void IsValidWebUrl_WhenCalled_ShouldBehavior()
        {

            // Arrange
            string validUrl = "https://www.example.com";
            string invalidUrl = "not-a-url";
            string nullUrl = null;
            string emptyUrl = "";
            string whitespaceUrl = "   ";
            string ftpUrl = "ftp://example.com";

            // Act
            var result1 = MainWindow.IsValidWebUrl(validUrl);
            var result2 = MainWindow.IsValidWebUrl(invalidUrl);
            var result3 = MainWindow.IsValidWebUrl(nullUrl);
            var result4 = MainWindow.IsValidWebUrl(emptyUrl);
            var result5 = MainWindow.IsValidWebUrl(whitespaceUrl);
            var result6 = MainWindow.IsValidWebUrl(ftpUrl);

            // Assert
            result1.Should().BeTrue();
            result2.Should().BeFalse();
            result3.Should().BeFalse();
            result4.Should().BeFalse();
            result5.Should().BeFalse();
            result6.Should().BeFalse();

        }
    }
}
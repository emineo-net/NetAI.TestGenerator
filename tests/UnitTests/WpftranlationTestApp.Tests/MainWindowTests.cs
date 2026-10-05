using FluentAssertions;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;
namespace WpftranlationTestApp.Tests
{
    public class MainWindowTests
    {
        [StaFact]
        public void IsValidWebUrl_WhenCalled_ShouldBehavior()
        {
            // Arrange

            // Act
            bool result = MainWindow.IsValidWebUrl("https://www.example.com");

            // Assert
            result.Should().BeTrue();
        }
    }
}
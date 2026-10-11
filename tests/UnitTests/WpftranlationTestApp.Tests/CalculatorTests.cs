using FluentAssertions;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;
namespace WpftranlationTestApp.Tests
{
    public class CalculatorTests
    {
        private readonly Calculator _sut;

        public CalculatorTests()
        {
            _sut = new Calculator();
        }

        [Fact]
        public void Reverse_WhenCalled_ShouldReturnReversedString()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            string input = "hello";

            // Act
            string result = _sut.Reverse(input);

            // Assert
            result.Should().Be("olleh");

            // =========================================================================
        }
    }
}
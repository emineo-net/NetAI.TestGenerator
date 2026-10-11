using Moq;
using System;
using System.Threading.Tasks;
using Xunit;
namespace WpftranlationTestApp.Tests
{
    public class CalculatorTests
    {
        private readonly Calculator _calculator;

        public CalculatorTests()
        {
            _calculator = new Calculator();
        }

        [Fact]
        public void Multiply_PositiveNumbers_ProductIsCorrect()
        {
            // Arrange
            int a = 5;
            int b = 6;

            // Act
            var result = _calculator.Multiply(a, b);

            // Assert
            Assert.Equal(30, result);
        }
    }
}
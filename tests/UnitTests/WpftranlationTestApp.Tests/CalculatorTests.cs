using Xunit;
using Moq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using WpftranlationTestApp;
using System.Reflection;
namespace WpftranlationTestApp.Tests
{
    public class CalculatorTests
    {
        [Fact]
        public void Add_PositiveNumbers_ReturnsSum()
        {
            var calculator = new Calculator();

            int result = calculator.Add(2, 3);

            Assert.Equal(5, result);
        }
        [Fact]
        public void Subtract_PositiveNumbers_ReturnsDifference()
        {
            var calculator = new Calculator();

            int result = calculator.Subtract(10, 5);

            Assert.Equal(5, result);
        }
        [Fact]
        public void Divide_ValidInputs_ReturnsCorrectResult()
        {
            // Arrange
            var calculator = new Calculator();
            double a = 10;
            double b = 2;

            // Act
            double result = calculator.Divide(a, b);

            // Assert
            Assert.Equal(5, result);
        }
        [Fact]
        public void Reverse_EmptyString_ReturnsEmptyString()
        {
            var calculator = new Calculator();
            string result = calculator.Reverse(string.Empty);

            Assert.Equal(string.Empty, result);
        }
        [Fact]
        public void IsPalindrome_EmptyString_ReturnsTrue()
        {
            var calculator = new WpftranlationTestApp.Calculator();
            bool result = calculator.IsPalindrome(string.Empty);
            Assert.True(result);
        }
        [Fact]
        public void GetEvenNumbers_ValidInput_ReturnsCorrectEvenNumbers()
        {
            // Arrange
            var calculator = new WpftranlationTestApp.Calculator();
            var numbers = new List<int> { 1, 2, 3, 4, 5, 6 };

            // Act
            var result = calculator.GetEvenNumbers(numbers);

            // Assert
            Assert.Equal(new List<int> { 2, 4, 6 }, result);
        }
        [Fact]
        public void Factorial_PositiveNumber_ReturnsCorrectFactorial()
        {
            // Arrange
            var calculator = new Calculator();

            // Act
            int result = calculator.Factorial(5);

            // Assert
            Assert.Equal(120, result);
        }
        [Fact]
        public async Task FetchGreetingAsync_EmptyName_ReturnsDefaultGreeting()
        {
            // Arrange
            var calculator = new Calculator();

            // Act
            string result = await calculator.FetchGreetingAsync(string.Empty);

            // Assert
            Assert.Equal("Hallo, Fremder!", result);
        }
        [Fact(Skip = "async void cannot be awaited reliably by a unit test")]
        public void FireAndForgetLog_IsNotDirectlyTestable()
        {
            // Required refactoring:
            // BEFORE:
            //   public async void FireAndForgetLog(string message)
            //   { /* original async body */ }
            //
            // AFTER:
            //   private async Task FireAndForgetLogAsync(string message)
            //   { /* original async body, now awaitable & testable */ }
            //
            //   public async void FireAndForgetLog(string message)
            //       => await FireAndForgetLogAsync(message);
            //
            // Rationale: the shim stays UI-bound by design and is excluded from unit tests;
            // the FireAndForgetLogAsync method carries all logic and is fully unit-testable.
        }
        [Fact]
        public void Multiply_IsNotDirectlyTestable()
        {
            // Required source refactorings:
            // - Make method 'Multiply' internal and add InternalsVisibleTo.

            var calculator = new WpftranlationTestApp.Calculator();
            var methodInfo = typeof(WpftranlationTestApp.Calculator).GetMethod("Multiply", BindingFlags.NonPublic | BindingFlags.Instance);
            if (methodInfo != null)
            {
                int result = (int)methodInfo.Invoke(calculator, new object[] { 2, 3 });
                Assert.Equal(6, result);
            }
            else
            {
                Assert.Fail("Method 'Multiply' not found.");
            }
        }
    }
}
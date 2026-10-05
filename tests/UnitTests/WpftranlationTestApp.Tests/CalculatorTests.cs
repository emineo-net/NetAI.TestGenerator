using Xunit;
using FluentAssertions;
using Moq;
using System;
using System.Threading.Tasks;
using System.Reflection;
namespace WpftranlationTestApp.Tests
{
    public class CalculatorTests
    {
        [Fact]
        public void Add_WhenCalled_ShouldReturnCorrectSum()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            int a = 5;
            int b = 3;

            // Act
            int result = _sut.Add(a, b);

            // Assert
            result.Should().Be(8);

            // =========================================================================
        }
        [Fact]
        public void Subtract_WhenCalled_ShouldReturnCorrectDifference()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            int a = 10;
            int b = 5;
            int expectedDifference = 5;

            // Act
            int result = _sut.Subtract(a, b);

            // Assert
            result.Should().Be(expectedDifference);

            // =========================================================================
        }
        [Fact]
        public void Divide_WhenCalled_ShouldBehavior()
        {
            // Arrange

            double a = 10;
            double b = 2;
            double expected = 5;

            // Act
            double result = _sut.Divide(a, b);

            // Assert
            result.Should().Be(expected);
        }
        [Fact]
        public void Reverse_WhenCalled_ShouldBehavior()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            string input = "hello";
            string expectedOutput = "olleh";

            // Act
            string result = _sut.Reverse(input);

            // Assert
            result.Should().Be(expectedOutput);

            // =========================================================================
        }
        [Fact]
        public void IsPalindrome_WhenCalled_ShouldBehavior()
        {
            // Arrange

            // Act

            // Assert

            // =========================================================================
        }
        [Fact]
        public void GetEvenNumbers_WhenCalled_ShouldReturnCorrectEvenNumbers()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            var numbers = new List<int> { 1, 2, 3, 4, 5, 6 };

            // Act
            var evenNumbers = _sut.GetEvenNumbers(numbers);

            // Assert
            evenNumbers.Should().BeEquivalentTo(new List<int> { 2, 4, 6 });

            // =========================================================================
        }
        [Fact]
        public void Factorial_WhenCalled_ShouldBehavior()
        {
            // Arrange

            // Act

            // Assert

            // =========================================================================
        }
        [Fact]
        public async Task FetchGreetingAsync_WhenCalled_ShouldBehavior()
        {
            // =========================================================================
            // AI AREA: Only the content between the markers is filled in by the AI.
            // =========================================================================

            // Arrange
            string name = "Alice";

            // Act
            string result = await _sut.FetchGreetingAsync(name);

            // Assert
            result.Should().Be("Hallo, Alice!");

            // =========================================================================
        }
        // REQUIRED SOURCE REFACTORING (do not implement the test yet):
        // =========================================================================
        // 1) Change 'async void' to 'async Task'.
        //
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
        // =========================================================================

        [Fact(Skip = "requires refactoring: async void method must become an awaitable async Task")]
        public void FireAndForgetLog_RequiresRefactoring()
        {
            // AI AREA: Describe the required refactoring above (comments only, no real test).
        }
        [Fact]
        public void Multiply_WhenCalled_ShouldReturnCorrectProduct()
        {
            // Arrange
            int a = 3;
            int b = 4;
            Type calculatorType = typeof(Calculator);
            MethodInfo multiplyMethod = calculatorType.GetMethod("Multiply", BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            int result = (int)multiplyMethod.Invoke(_sut, new object[] { a, b });

            // Assert
            Assert.Equal(12, result);
        }
    }
}
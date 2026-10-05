using FluentAssertions;
using Moq;
using System.Threading.Tasks;
using System;
using Xunit;

namespace WpftranlationTestApp.Tests
{
    public class CalculatorTests
    {
        private readonly Calculator _sut; public CalculatorTests()
        {
            _sut = new Calculator();
        }
        [Fact]
        public void Guard_WhenCalled_ShouldThrow()
        {
            Action act = () => _sut.Guard();
            act.Should().Throw<InvalidOperationException>();
        }
        [Fact]
        public void Add_WhenCalled_ShouldReturnCorrectSum()
        {

            // Arrange
            int a = 5;
            int b = 3;

            // Act
            int result = _sut.Add(a, b);

            // Assert
            result.Should().Be(8);

        }
        [Fact]
        public void Subtract_WhenCalled_ShouldReturnCorrectResult()
        {

            // Arrange
            int a = 10;
            int b = 5;

            // Act
            int result = _sut.Subtract(a, b);

            // Assert
            result.Should().Be(5);

        }
        [Fact]
        public void Divide_WhenCalled_ShouldBehavior()
        {

            // Arrange
            double a = 10;
            double b = 2;

            // Act
            double result = _sut.Divide(a, b);

            // Assert
            result.Should().Be(5);

        }
        [Fact]
        public void Reverse_WhenCalled_ShouldBehavior()
        {

            // Arrange
            string input = "hello";

            // Act
            string result = _sut.Reverse(input);

            // Assert
            result.Should().Be("olleh");

        }
        [Fact]
        public void IsPalindrome_WhenCalled_ShouldBehavior()
        {

            // Arrange
            string input = "radar";

            // Act
            bool result = _sut.IsPalindrome(input);

            // Assert
            result.Should().BeTrue();

        }
        [Fact]
        public void GetEvenNumbers_WhenCalled_ShouldReturnCorrectEvenNumbers()
        {

            // Arrange
            var numbers = new List<int> { 1, 2, 3, 4, 5, 6 };

            // Act
            var result = _sut.GetEvenNumbers(numbers);

            // Assert
            result.Should().BeEquivalentTo(new List<int> { 2, 4, 6 });

        }
        [Fact]
        public void Factorial_WhenCalled_ShouldBehavior()
        {

            // Arrange
            int number = 5;
            int expectedFactorial = 120;

            // Act
            int result = _sut.Factorial(number);

            // Assert
            result.Should().Be(expectedFactorial);

        }
        [Fact]
        public async Task FetchGreetingAsync_WhenCalled_ShouldReturnGreeting()
        {
            // Arrange
            string name = "Alice";

            // Act
            string result = await _sut.FetchGreetingAsync(name);

            // Assert
            result.Should().Be("Hallo, Alice!");
        }// REQUIRED SOURCE REFACTORING (do not implement the test yet):

        // 1) Change 'async void' to 'async Task'.


        [Fact(Skip = "requires refactoring: async void method must become an awaitable async Task")]
        public void FireAndForgetLog_RequiresRefactoring()
        {
            // BEFORE:
            //   public async void FireAndForgetLog(string message)
            //   { /* original async body */ }
            //
            // AFTER:
            //   public async Task FireAndForgetLogAsync(string message)
            //   { /* original async body, now awaitable &amp; testable */ }
            //
            // Rationale: the method becomes fully unit-testable.
        }
        [Fact]
        public void Multiply_WhenCalled_ShouldBehavior()
        {

            // Arrange
            int a = 3;
            int b = 4;
            int expected = 12;

            // Act
            var methodInfo = typeof(Calculator).GetMethod("Multiply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (methodInfo == null)
            {
                throw new Exception("Method 'Multiply' not found.");
            }
            int result = (int)methodInfo.Invoke(_sut, new object[] { a, b });

            // Assert
            result.Should().Be(expected);

        }
    }
}
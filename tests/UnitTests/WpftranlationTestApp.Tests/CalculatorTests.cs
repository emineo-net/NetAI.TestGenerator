using FluentAssertions;
using Moq;
using System.Reflection;
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
        public void Add_WhenCalled_ShouldReturnSumOfTwoIntegers()
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
        public void Subtract_WhenCalled_ShouldReturnDifferenceOfTwoIntegers()
        {

            // Arrange
            int a = 10;
            int b = 5;

            // Act
            var result = _sut.Subtract(a, b);

            // Assert
            result.Should().Be(5);

        }
        [Fact]
        public void Divide_WhenCalled_ShouldBehavior()
        {

            // Arrange
            double a = 10.0;
            double b = 2.0;
            double expected = 5.0;

            // Act
            double result = _sut.Divide(a, b);

            // Assert
            result.Should().Be(expected);

        }
        [Fact]
        public void Reverse_WhenCalled_ShouldReturnReversedString()
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
            var input = "racecar";

            // Act
            var result = _sut.IsPalindrome(input);

            // Assert
            result.Should().BeTrue();

        }
        [Fact]
        public void GetEvenNumbers_WhenCalled_ShouldReturnEvenNumbersOnly()
        {

            // Arrange
            var inputNumbers = new List<int> { 1, 2, 3, 4, 5, 6 };
            var expectedEvenNumbers = new List<int> { 2, 4, 6 };

            // Act
            var result = _sut.GetEvenNumbers(inputNumbers);

            // Assert
            result.Should().ContainInOrder(expectedEvenNumbers);
            result.Count.Should().Be(3);

        }
        [Fact]
        public void Factorial_WhenCalled_ShouldBehavior()
        {

            // Arrange
            int input = 5;
            int expected = 120;

            // Act
            int result = _sut.Factorial(input);

            // Assert
            result.Should().Be(expected);

        }
        [Fact]
        public async Task FetchGreetingAsync_WhenCalled_ShouldReturnExpectedGreeting()
        {

            // Arrange
            var name = "TestUser";

            // Act
            var result = await _sut.FetchGreetingAsync(name);

            // Assert
            result.Should().Be("Hallo, TestUser!");

        }// REQUIRED SOURCE REFACTORING (do not implement the test yet):

        // 1) Change 'async void' to 'async Task'.

        // 

        // BEFORE:

        //   private async void FireAndForgetLog(object sender, RoutedEventArgs e)

        //   { /* original async body */ }

        //

        // AFTER:

        //   private async void FireAndForgetLog(object sender, RoutedEventArgs e)

        //       => await FireAndForgetLogAsync();

        //

        //   private async Task FireAndForgetLogAsync()

        //   { /* original async body, now awaitable & testable */ }

        //

        // Rationale: the shim stays UI-bound by design and is excluded from unit tests;

        // the FireAndForgetLogAsync method carries all logic and is fully unit-testable.


        [Fact(Skip = "requires refactoring: async void method must become an awaitable async Task")]
        public void FireAndForgetLog_RequiresRefactoring()
        {
        }
        [Fact]
        public void Multiply_IsNotDirectlyTestable_RequiresReflection()
        {

            // Arrange
            var methodInfo = typeof(Calculator).GetMethod("Multiply", BindingFlags.NonPublic | BindingFlags.Instance);
            var args = new object[] { 3, 4 };

            // Act
            var result = methodInfo.Invoke(_sut, args);

            // Assert
            result.Should().Be(12);

        }
    }
}
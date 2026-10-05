

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WpftranlationTestApp
{
    public class Calculator
    {
        public void Guard() => throw new InvalidOperationException("not allowed");
        public int Add(int a, int b)
        {
            return a + b;
        }

        public int Subtract(int a, int b)
        {
            return a - b;
        }

        public double Divide(double a, double b)
        {
            if (b == 0)
                throw new DivideByZeroException("Division durch Null ist nicht erlaubt.");
            return a / b;
        }

        public string Reverse(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            return new string(input.Reverse().ToArray());
        }

        public bool IsPalindrome(string input)
        {
            if (string.IsNullOrEmpty(input))
                return true;
            var reversed = new string(input.Reverse().ToArray());
            return string.Equals(input, reversed, StringComparison.OrdinalIgnoreCase);
        }

        public List<int> GetEvenNumbers(IEnumerable<int> numbers)
        {
            if (numbers == null)
                throw new ArgumentNullException(nameof(numbers));
            return numbers.Where(n => n % 2 == 0).ToList();
        }

        public int Factorial(int n)
        {
            if (n < 0)
                throw new ArgumentOutOfRangeException(nameof(n), "n muss >= 0 sein.");
            if (n == 0 || n == 1)
                return 1;
            return n * Factorial(n - 1);
        }

        public async Task<string> FetchGreetingAsync(string name)
        {
            await Task.Delay(10);
            if (string.IsNullOrWhiteSpace(name))
                return "Hallo, Fremder!";
            return $"Hallo, {name}!";
        }

        public async void FireAndForgetLog(string message)
        {
            await Task.Delay(10);
            LastLog = message;
        }

        public string LastLog { get; private set; } = string.Empty;

        private int Multiply(int a, int b)
        {
            return a * b;
        }
    }
}

using Moq;
using System.Threading.Tasks;
using System;
using Xunit;

namespace WpftranlationTestApp.Tests
{
    public class MainWindowTests
    {
        // REQUIRED SOURCE REFACTORING (do not implement the test yet):

        // 1) Make method 'internal' and add InternalsVisibleTo.

        // 2) Change 'async void' to 'async Task'.

        // 3) Inject 'IFileSystem' (package 'System.IO.Abstractions') instead of static 'System.IO.File'.


        [StaFact(Skip = "requires refactoring: async void method must become an awaitable async Task")]
        public void TestButton_OnClick_RequiresRefactoring()
        {
            // 1) Make method 'internal' and add InternalsVisibleTo.
            // 2) Change 'async void' to 'async Task'.
            // 3) Inject 'IFileSystem' (package 'System.IO.Abstractions') instead of static 'System.IO.File'.
            //
            // Suggested refactoring pattern:
            // BEFORE:
            //   private async void TestButton_OnClick(object sender, RoutedEventArgs e)
            //   { /* original async body */ }
            //
            // AFTER:
            //   private async void TestButton_OnClick(object sender, RoutedEventArgs e)
            //       => await TestButton_OnClickAsync();
            //
            //   private async Task TestButton_OnClickAsync()
            //   { /* original async body, now awaitable & testable */ }
            //
            // Rationale: the shim stays UI-bound by design and is excluded from unit tests;
            // the TestButton_OnClickAsync method carries all logic and is fully unit-testable.
        }
    }
}
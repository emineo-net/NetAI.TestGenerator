using System;
using System.Threading.Tasks;
using Xunit;
namespace NetAI.Generated.Tests
{
    public class MainWindowTests
    {
        [Fact(Skip = "Methode ist 'private' und kann nicht direkt aufgerufen werden.")]
        public async Task TestButton_OnClick_ShouldExecuteAsExpected()
        {
            // Arrange
            var mainWindow = new MainWindow();
            var sender = new object();
            var e = new RoutedEventArgs();

            // Act & Assert (not possible due to private method and async void)
        }
    }
}
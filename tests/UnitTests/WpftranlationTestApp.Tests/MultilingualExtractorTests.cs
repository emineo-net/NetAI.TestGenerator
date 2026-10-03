using Xunit;
using Moq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
namespace WpftranlationTestApp.Tests
{
    public class MultilingualExtractorTests
    {
        [Fact]
        public async Task ExtractEntitiesAsync_ValidText_ReturnsCorrectCount()
        {
            // Arrange
            var extractor = new MultilingualExtractor();
            var text = "Some valid text with prices, addresses, dates, and headings.";

            // Act
            int result = await extractor.ExtractEntitiesAsync(text);

            // Assert
            Assert.Equal(2, result);
        }
        [Fact]
        public void ExtractEmails_ShouldExtractEmailsFromText()
        {
            // Arrange
            var text = "Please contact us at support@example.com or sales@example.org.";
            var extractorType = typeof(MultilingualExtractor);
            var method = extractorType.GetMethod("ExtractEmails", BindingFlags.NonPublic | BindingFlags.Static);

            // Act
            var result = (List<string>)method.Invoke(null, new object[] { text });

            // Assert
            Assert.NotNull(result);
            Assert.Contains("support@example.com", result);
            Assert.Contains("sales@example.org", result);
        }
    }
}
using FluentAssertions;
using Moq;
using System.Reflection;
using System.Threading.Tasks;
using System;
using Xunit;

namespace WpftranlationTestApp.Tests;

public class MultilingualExtractorTests
{
    private readonly MultilingualExtractor _sut;

    public MultilingualExtractorTests()
            {
                _sut = new MultilingualExtractor();
            }

    [Fact]
            public async Task ExtractEntitiesAsync_WhenCalled_ShouldReturnTwo()
            {
                // =========================================================================
                // AI AREA: Only the content between the markers is filled in by the AI.
                // =========================================================================
    
                // Arrange
                var text = "Sample text for extraction";
    
                // Act
                var result = await _sut.ExtractEntitiesAsync(text);
    
                // Assert
                result.Should().Be(2);
    
                // =========================================================================
            }

    [Fact]
            public void ExtractEmails_WhenCalled_ShouldBehavior()
            {
                // =========================================================================
                // AI AREA: Only the content between the markers is filled in by the AI.
                // =========================================================================
    
                // Arrange
                var text = "Please contact us at support@example.com or sales@example.org.";
                var expectedEmails = new List<string> { "support@example.com", "sales@example.org" };
    
                // Act
                var emails = (List<string>)typeof(MultilingualExtractor)
                    .GetMethod("ExtractEmails", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new object[] { text });
    
                // Assert
                emails.Should().BeEquivalentTo(expectedEmails);
    
                // =========================================================================
            }
}

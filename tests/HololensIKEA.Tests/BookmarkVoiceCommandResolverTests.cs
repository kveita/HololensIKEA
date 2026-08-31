using System.Collections.Generic;
using HololensIKEA.Models;
using HololensIKEA.Services;
using Xunit;

namespace HololensIKEA.Tests
{
    public class BookmarkVoiceCommandResolverTests
    {
        private static readonly List<Bookmark> Bookmarks = new List<Bookmark>
        {
            new Bookmark { Name = "BILLY Bookcase" },
            new Bookmark { Name = "KALLAX Shelf Unit" }
        };

        [Theory]
        [InlineData("BILLY")]
        [InlineData("billy")]
        [InlineData(" add   BILLY ")]
        [InlineData("load BILLY")]
        public void FindBookmark_RecognizedAlias_ReturnsMatchingBookmark(string recognizedText)
        {
            var bookmark = BookmarkVoiceCommandResolver.FindBookmark(recognizedText, Bookmarks);

            Assert.NotNull(bookmark);
            Assert.Equal("BILLY Bookcase", bookmark.Name);
        }

        [Fact]
        public void FindBookmark_UnknownPhrase_ReturnsNull()
        {
            Assert.Null(BookmarkVoiceCommandResolver.FindBookmark("a bookcase", Bookmarks));
        }

        [Fact]
        public void FindBookmark_AmbiguousAlias_ReturnsNull()
        {
            var ambiguous = new List<Bookmark>
            {
                new Bookmark { Name = "BILLY Bookcase" },
                new Bookmark { Name = "BILLY Shelf" }
            };

            Assert.Null(BookmarkVoiceCommandResolver.FindBookmark("BILLY", ambiguous));
        }
    }
}
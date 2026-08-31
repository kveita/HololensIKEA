using System;
using System.Collections.Generic;
using System.Linq;
using HololensIKEA.Models;

namespace HololensIKEA.Services
{
    public static class BookmarkVoiceCommandResolver
    {
        private static readonly string[] CommandPrefixes =
        {
            "add ", "load ", "show ", "product "
        };

        public static Bookmark FindBookmark(string recognizedText, IEnumerable<Bookmark> bookmarks)
        {
            if (string.IsNullOrWhiteSpace(recognizedText) || bookmarks == null)
                return null;

            var command = Normalize(recognizedText);
            foreach (var prefix in CommandPrefixes)
            {
                if (command.StartsWith(prefix, StringComparison.Ordinal))
                {
                    command = command.Substring(prefix.Length).Trim();
                    break;
                }
            }

            var candidates = bookmarks
                .Where(bookmark => bookmark != null && !string.IsNullOrWhiteSpace(bookmark.Name))
                .Where(bookmark => GetAlias(bookmark.Name) == command)
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        public static string GetAlias(string bookmarkName)
        {
            if (string.IsNullOrWhiteSpace(bookmarkName))
                return string.Empty;

            return Normalize(bookmarkName).Split(' ')[0];
        }

        private static string Normalize(string text)
        {
            return string.Join(" ", text.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
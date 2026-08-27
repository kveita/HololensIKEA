using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Storage;
using HololensIKEA.Models;

namespace HololensIKEA.Services
{
    /// <summary>
    /// Loads and manages the list of bookmarked IKEA products from bookmarks.json.
    /// The bookmarks file is embedded in the app package and loaded at startup.
    /// </summary>
    public sealed class BookmarksService : IDisposable
    {
        private List<Bookmark> _bookmarks = new List<Bookmark>();
        private bool _loaded = false;
        private object _loadLock = new object();

        public int Count => _bookmarks.Count;

        public IReadOnlyList<Bookmark> Bookmarks => _bookmarks.AsReadOnly();

        /// <summary>
        /// Gets a bookmark by index.
        /// </summary>
        public Bookmark GetAt(int index)
        {
            if (index >= 0 && index < _bookmarks.Count)
                return _bookmarks[index];
            return null;
        }

        /// <summary>
        /// Finds a bookmark by name (case-insensitive).
        /// </summary>
        public Bookmark FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return _bookmarks.FirstOrDefault(b =>
                b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Searches bookmarks by keyword (case-insensitive).
        /// Returns up to maxResults matches.
        /// </summary>
        public List<Bookmark> Search(string keyword, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<Bookmark>(_bookmarks);

            string k = keyword.ToLowerInvariant();
            var results = _bookmarks
                .Where(b => b.Name.ToLowerInvariant().Contains(k))
                .Take(maxResults)
                .ToList();

            Debug.WriteLine($"[Bookmarks] Search for '{keyword}' returned {results.Count} results");
            return results;
        }

        /// <summary>
        /// Loads bookmarks from the embedded bookmarks.json resource.
        /// Must be called before accessing any bookmarks.
        /// </summary>
        public async Task LoadAsync()
        {
            if (_loaded)
                return;

            lock (_loadLock)
            {
                if (_loaded)
                    return;

                Task.Run(async () =>
                {
                    try
                    {
                        var file = await ApplicationData.Current.LocalFolder
                            .GetFileAsync("bookmarks.json");

                        var json = await FileIO.ReadTextAsync(file);
                        _bookmarks = JsonConvert.DeserializeObject<List<Bookmark>>(json)
                            ?? new List<Bookmark>();

                        Debug.WriteLine($"[Bookmarks] Loaded {_bookmarks.Count} bookmarks");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Bookmarks] Error loading: {ex.Message}");
                        // Fallback: try manifest resource
                        await LoadFromManifestAsync();
                    }
                    finally
                    {
                        _loaded = true;
                    }
                }).Wait();
            }
        }

        private async Task LoadFromManifestAsync()
        {
            try
            {
                // Try loading from the package directory
                var packageFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
                var file = await packageFolder.GetFileAsync("bookmarks.json");
                var json = await FileIO.ReadTextAsync(file);
                _bookmarks = JsonConvert.DeserializeObject<List<Bookmark>>(json)
                    ?? new List<Bookmark>();
                Debug.WriteLine($"[Bookmarks] Loaded {_bookmarks.Count} bookmarks from package");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bookmarks] Failed to load from package: {ex.Message}");
                // Return empty list - app will show no bookmarks
                _bookmarks = new List<Bookmark>();
            }
            _loaded = true;
        }

        public void Dispose()
        {
            _bookmarks?.Clear();
            _bookmarks = null;
        }
    }
}
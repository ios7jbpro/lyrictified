using System.IO;
using System.Text.Json;
using Lyrictified.Models;

namespace Lyrictified.Services;

public sealed class LyricsCacheService
{
    private string _cacheDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    private static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lyrictified", "cache");

    public LyricsCacheService()
    {
        _cacheDirectory = DefaultCacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public void SetCacheDirectory(string directory)
    {
        _cacheDirectory = directory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    private static string SanitizeKey(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            result[i] = invalid.Contains(s[i]) ? '_' : s[i];
        }
        return new string(result);
    }

    private string GetCachePath(string title, string artist)
    {
        var key = SanitizeKey($"{artist}__{title}");
        return Path.Combine(_cacheDirectory, $"{key}.json");
    }

    public IReadOnlyList<LyricLine>? TryGet(string title, string artist)
    {
        var path = GetCachePath(title, artist);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json, _jsonOptions);
            if (entry is null)
                return null;

            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return entry.Lines;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    public void Store(string title, string artist, IReadOnlyList<LyricLine> lines, int maxCacheSize)
    {
        if (maxCacheSize <= 0 || lines.Count == 0)
            return;

        try
        {
            var path = GetCachePath(title, artist);
            var entry = new CacheEntry { Lines = lines, CachedAt = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            File.WriteAllText(path, json);
            Evict(maxCacheSize);
        }
        catch { }
    }

    public void Clear()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_cacheDirectory, "*.json"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private void Evict(int maxCacheSize)
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();

            while (files.Count > maxCacheSize)
            {
                try { files[^1].Delete(); } catch { }
                files.RemoveAt(files.Count - 1);
            }
        }
        catch { }
    }

    private sealed class CacheEntry
    {
        public IReadOnlyList<LyricLine> Lines { get; set; } = Array.Empty<LyricLine>();
        public DateTime CachedAt { get; set; }
    }
}
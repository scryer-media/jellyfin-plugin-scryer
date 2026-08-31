using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Scryer.Services;

public class RequestAttributionStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _map;

    public RequestAttributionStore(IApplicationPaths applicationPaths)
    {
        var dir = Path.Combine(applicationPaths.PluginsPath, "Scryer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "request-attribution.json");
        _map = Load();
    }

    public void Record(string requestId, string jellyfinUserId)
    {
        lock (_lock)
        {
            _map[requestId] = jellyfinUserId;
            Save();
        }
    }

    public bool BelongsTo(string requestId, string jellyfinUserId)
    {
        lock (_lock)
        {
            return _map.TryGetValue(requestId, out var owner) && owner == jellyfinUserId;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>();
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}

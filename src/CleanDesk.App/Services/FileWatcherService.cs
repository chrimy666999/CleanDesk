using CleanDesk.App.Models;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using ThreadingTimer = System.Threading.Timer;

namespace CleanDesk.App.Services;

public sealed class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private ThreadingTimer? _timer;

    public FileWatcherService(Dispatcher dispatcher, AppSettings settings, IEnumerable<string> roots, Action onChanged)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _onChanged = onChanged;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
            };
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!_settings.RealtimeOrganize && !_settings.AutoOrganizeNewFiles)
        {
            return;
        }

        _timer?.Dispose();
        _timer = new ThreadingTimer(_ =>
        {
            _dispatcher.BeginInvoke(_onChanged);
        }, null, 700, Timeout.Infinite);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}

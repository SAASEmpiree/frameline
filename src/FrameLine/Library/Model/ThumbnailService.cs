using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FrameLine.Library;

internal sealed record ThumbResult(BitmapSource? Image, (int W, int H)? PixelSize, bool Abandoned);

/// Bounded thumbnail pipeline (port of CaptureLibrary's OperationQueue with maxConcurrent = 2:
/// one unbounded task per capture saturated the pool and decoded gigabytes at once with ~2,400
/// captures). Requests are served NEWEST FIRST: when the user scrolls, the rows on screen now
/// win over the ones that scrolled away, and those are skipped if no view still wants them.
internal sealed class ThumbnailService
{
    public static ThumbnailService Shared { get; } = new();
    private const int MaxConcurrent = 2;

    private sealed record Job(Func<bool> Wanted, Func<ThumbResult> Work, Action<ThumbResult> Done, Dispatcher Dispatcher);

    private readonly object _lock = new();
    private readonly LinkedList<Job> _pending = new();
    private int _running;

    public void Request(Func<bool> wanted, Func<ThumbResult> work, Action<ThumbResult> done, Dispatcher dispatcher)
    {
        lock (_lock) _pending.AddFirst(new Job(wanted, work, done, dispatcher));
        Pump();
    }

    private void Pump()
    {
        lock (_lock)
        {
            while (_running < MaxConcurrent && _pending.First is { } node)
            {
                _pending.RemoveFirst();
                _running++;
                var job = node.Value;
                Task.Run(() => Run(job));
            }
        }
    }

    private void Run(Job job)
    {
        ThumbResult result;
        try { result = job.Wanted() ? job.Work() : new ThumbResult(null, null, true); }
        catch { result = new ThumbResult(null, null, false); }
        lock (_lock) _running--;
        job.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => job.Done(result));
        Pump();
    }
}

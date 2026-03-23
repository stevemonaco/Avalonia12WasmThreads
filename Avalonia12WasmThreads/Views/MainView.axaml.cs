using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace Avalonia12WasmThreads.Views;

public partial class MainView : UserControl
{
    private readonly Rectangle[] _squares;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;
    private AvaloniaList<string> _log = new();
    private int _cycle;

    public MainView()
    {
        InitializeComponent();

        logItems.ItemsSource = _log;
        
        IBrush[] _palette =
        [
            Brushes.Coral, Brushes.DodgerBlue, Brushes.MediumSeaGreen, Brushes.Gold, Brushes.Orchid,
            Brushes.Tomato, Brushes.SteelBlue, Brushes.LimeGreen, Brushes.Orange, Brushes.HotPink
        ];

        _squares = new Rectangle[10];
        
        for (int i = 0; i < _squares.Length; i++)
        {
            _squares[i] = new Rectangle
            {
                Width = 60,
                Height = 60,
                Fill = _palette[i % _palette.Length]
            };
            
            Canvas.SetLeft(_squares[i], 80 * i + 20);
            Canvas.SetTop(_squares[i], 20);
            Arena.Children.Add(_squares[i]);
        }
    }

    private async void OnRun(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RunBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Spin up several background threads that continuously allocate memory,
        // forcing WebAssembly.Memory.grow() which invalidates TypedArray views
        // on all threads — including the one SkiaSharp is rendering on.
        var allocators = new Task[4];
        for (int t = 0; t < allocators.Length; t++)
        {
            int threadId = t;
            allocators[t] = Task.Run(() => MemoryPressureWorker(threadId, ct), ct);
        }

        // Also spin up background tasks that do concurrent dictionary/collection work
        // to add more cross-thread contention
        var bag = new ConcurrentBag<byte[]>();
        var contention = Task.Run(() => CollectionContentionWorker(bag, ct), ct);

        for (_cycle = 0; _cycle < 20; _cycle++)
        {
            Status.Text = $"Cycle {_cycle}: animating + background allocs...";

            // Animate multiple squares simultaneously to keep SkiaSharp busy rendering
            var animTasks = new List<Task>();
            for (int i = 0; i < _squares.Length; i++)
            {
                var sq = _squares[i];
                double targetX = _rng.Next(20, 500), targetY = _rng.Next(20, 300);
                animTasks.Add(AnimateSquare(sq, targetX, targetY));
            }

            // Also kick off a few Task.Run jobs that allocate and return results,
            // simulating real app patterns (deserializing API responses, etc.)
            var bgResults = new Task<string>[3];
            for (int j = 0; j < bgResults.Length; j++)
            {
                int jobId = j;
                int cycle = _cycle;
                bgResults[j] = Task.Run(() => BackgroundWorkWithAlloc(cycle, jobId), ct);
            }

            try
            {
                await Task.WhenAll(animTasks);
                await Task.WhenAll(bgResults);
                var results = string.Join(", ", Array.ConvertAll(bgResults, t => t.Result));
                AppendLog($"Cycle {_cycle}: [{results}]");
            }
            catch (Exception ex)
            {
                AppendLog($"Cycle {_cycle}: FAILED — {ex.Message}");
            }

            // Trigger GC to further stress the runtime while rendering
            if (_cycle % 3 == 0)
            {
                _ = Task.Run(() =>
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, true);
                    GC.WaitForPendingFinalizers();
                });
            }
        }

        _cts.Cancel();
        try { await Task.WhenAll(allocators); } catch (OperationCanceledException) { }
        try { await contention; } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;

        Status.Text = "Done — all cycles complete.";
        RunBtn.IsEnabled = true;
    }

    /// <summary>
    /// Continuously allocates and discards byte arrays on a background thread.
    /// This forces WebAssembly.Memory.grow() calls which refresh TypedArray views.
    /// </summary>
    private void MemoryPressureWorker(int id, CancellationToken ct)
    {
        var rng = new Random(id * 31);
        var keepAlive = new List<byte[]>();

        while (!ct.IsCancellationRequested)
        {
            // Allocate arrays of varying sizes — larger ones are more likely to trigger grow()
            int size = rng.Next(1, 5) switch
            {
                1 => rng.Next(1024, 8192),             // small
                2 => rng.Next(8192, 65536),             // medium
                3 => rng.Next(65536, 524288),           // large
                _ => rng.Next(524288, 2 * 1024 * 1024), // very large — likely triggers grow()
            };

            var buf = new byte[size];
            // Touch the memory so it's not optimized away
            buf[0] = (byte)id;
            buf[size / 2] = 0xAB;
            buf[size - 1] = 0xFF;

            keepAlive.Add(buf);

            // Periodically discard to create GC pressure
            if (keepAlive.Count > 50)
            {
                keepAlive.RemoveRange(0, 40);
            }

            // Small yield to not completely starve other threads but stay aggressive
            if (rng.Next(10) == 0)
                Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Adds cross-thread collection contention alongside memory pressure.
    /// </summary>
    private void CollectionContentionWorker(ConcurrentBag<byte[]> bag, CancellationToken ct)
    {
        var rng = new Random(42);
        while (!ct.IsCancellationRequested)
        {
            // Add allocations to shared collection
            bag.Add(new byte[rng.Next(4096, 32768)]);

            // Drain periodically
            if (bag.Count > 100)
            {
                while (bag.TryTake(out _)) { }
            }

            if (rng.Next(5) == 0)
                Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Simulates background work (like deserializing an API response) that allocates
    /// memory and returns a result back to the UI thread.
    /// </summary>
    private string BackgroundWorkWithAlloc(int cycle, int jobId)
    {
        var rng = new Random(cycle * 100 + jobId);

        // Simulate deserializing a large response — allocate strings and collections
        var items = new List<string>();
        for (int i = 0; i < rng.Next(100, 500); i++)
        {
            items.Add(new string((char)('A' + rng.Next(26)), rng.Next(50, 200)));
        }

        // Allocate a large-ish byte array (simulating image/binary data)
        var payload = new byte[rng.Next(32768, 262144)];
        rng.NextBytes(payload);

        // Simulate some CPU work
        int hash = 0;
        for (int i = 0; i < payload.Length; i++)
            hash = hash * 31 + payload[i];

        //Thread.Sleep(rng.Next(10, 50));
        return $"j{jobId}:{items.Count}items,h={hash & 0xFFFF:X4}";
    }

    private async Task AnimateSquare(Rectangle sq, double toX, double toY)
    {
        var dur = TimeSpan.FromMilliseconds(200 + _rng.Next(300));

        var xAnim = new DoubleTransition { Property = Canvas.LeftProperty, Duration = dur, Easing = new CubicEaseInOut() };
        var yAnim = new DoubleTransition { Property = Canvas.TopProperty, Duration = dur, Easing = new CubicEaseInOut() };

        sq.Transitions = new Transitions { xAnim, yAnim };

        Canvas.SetLeft(sq, toX);
        Canvas.SetTop(sq, toY);

        await Task.Delay(dur + TimeSpan.FromMilliseconds(50));
    }

    private void AppendLog(string msg)
    {
        _log.Insert(0, msg);
    }
}

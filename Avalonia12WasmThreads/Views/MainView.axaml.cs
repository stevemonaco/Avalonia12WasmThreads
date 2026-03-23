using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace Avalonia12WasmThreads.Views;

public partial class MainView : UserControl
{
    private CancellationTokenSource? _cts;
    private readonly AvaloniaList<string> _log = new();

    public MainView()
    {
        InitializeComponent();
        logItems.ItemsSource = _log;
    }

    private async void OnRun(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RunBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        AppendLog("Starting SkiaSharp handle contention test...");
        AppendLog("Background threads will create/dispose SkiaSharp objects");
        AppendLog("while the render thread accesses SKSurface.Canvas.");

        // Launch background threads that churn SkiaSharp handles.
        // This creates contention on HandleDictionary's ReaderWriterLockSlim —
        // the same lock the render thread needs when it calls SKSurface.get_Canvas().
        var workers = new Task[6];
        for (int i = 0; i < workers.Length; i++)
        {
            int id = i;
            workers[i] = Task.Run(() => SkiaHandleChurnWorker(id, ct), ct);
        }

        // Drive rendering hard by continuously invalidating a visual that
        // uses RenderTargetBitmap (forces SkiaSharp surface/canvas creation on render thread)
        var renderDriver = Task.Run(() => RenderInvalidationDriver(ct), ct);

        // Also add memory pressure to trigger WebAssembly.Memory.grow(),
        // which invalidates ArrayBuffer views and compounds the issue
        var memWorker = Task.Run(() => MemoryPressureWorker(ct), ct);

        int cycle = 0;
        try
        {
            while (!ct.IsCancellationRequested && cycle < 60)
            {
                Status.Text = $"Cycle {cycle}: contending on SkiaSharp handles...";
                await Task.Delay(500, ct);
                cycle++;

                if (cycle % 5 == 0)
                    AppendLog($"Cycle {cycle}: still running, {workers.Length} handle-churn threads active");
            }
        }
        catch (OperationCanceledException) { }

        _cts.Cancel();
        try { await Task.WhenAll(workers); } catch { }
        try { await renderDriver; } catch { }
        try { await memWorker; } catch { }
        _cts.Dispose();
        _cts = null;

        Status.Text = $"Done — completed {cycle} cycles without crash.";
        AppendLog($"Test finished after {cycle} cycles.");
        RunBtn.IsEnabled = true;
    }

    /// <summary>
    /// Churns SkiaSharp object handles on a background thread.
    /// Each create/dispose goes through HandleDictionary.GetOrAddObject / Remove,
    /// which acquires the ReaderWriterLockSlim that the render thread also needs.
    /// This directly reproduces the contention path:
    ///   SKSurface.get_Canvas() → HandleDictionary.GetOrAddObject → RWLock
    /// </summary>
    private void SkiaHandleChurnWorker(int id, CancellationToken ct)
    {
        var rng = new Random(id * 37);
        int ops = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Vary the operations to hit different HandleDictionary code paths
                switch (rng.Next(5))
                {
                    case 0:
                    {
                        // SKBitmap → creates handle entry
                        using var bmp = new SKBitmap(rng.Next(64, 512), rng.Next(64, 512));
                        using var canvas = new SKCanvas(bmp);
                        using var paint = new SKPaint { Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)) };
                        canvas.DrawRect(0, 0, bmp.Width, bmp.Height, paint);
                        canvas.Flush();
                        break;
                    }
                    case 1:
                    {
                        // SKSurface → same path as render thread (SKSurface.Canvas)
                        var info = new SKImageInfo(rng.Next(100, 400), rng.Next(100, 400));
                        using var surface = SKSurface.Create(info);
                        if (surface != null)
                        {
                            var canvas = surface.Canvas; // This is the exact call that contends
                            using var paint = new SKPaint { Color = SKColors.Red };
                            canvas.DrawCircle(50, 50, 30, paint);
                            canvas.Flush();
                            using var snap = surface.Snapshot();
                        }
                        break;
                    }
                    case 2:
                    {
                        // Multiple small bitmaps — rapid handle create/destroy
                        var bitmaps = new List<SKBitmap>();
                        for (int j = 0; j < rng.Next(5, 20); j++)
                        {
                            bitmaps.Add(new SKBitmap(32, 32));
                        }
                        foreach (var b in bitmaps) b.Dispose();
                        break;
                    }
                    case 3:
                    {
                        // SKImage from bitmap — crosses handle dictionary entries
                        using var bmp = new SKBitmap(128, 128);
                        using var canvas = new SKCanvas(bmp);
                        using var paint = new SKPaint { Color = SKColors.Blue };
                        canvas.Clear(SKColors.White);
                        canvas.DrawRect(10, 10, 100, 100, paint);
                        using var img = SKImage.FromBitmap(bmp);
                        using var data = img.Encode(SKEncodedImageFormat.Png, 80);
                        break;
                    }
                    case 4:
                    {
                        // SKPath + SKPaint churn
                        using var path = new SKPath();
                        path.MoveTo(0, 0);
                        for (int p = 0; p < rng.Next(10, 50); p++)
                            path.LineTo(rng.Next(200), rng.Next(200));
                        path.Close();

                        using var paint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = rng.Next(1, 5),
                            Color = SKColors.Green
                        };

                        using var bmp = new SKBitmap(200, 200);
                        using var canvas = new SKCanvas(bmp);
                        canvas.DrawPath(path, paint);
                        break;
                    }
                }

                ops++;
            }
            catch (Exception)
            {
                // Swallow — we expect crashes from contention
            }

            // Minimal yielding to maximize contention
            if (rng.Next(20) == 0)
                Thread.Sleep(0);
        }
    }

    /// <summary>
    /// Continuously invalidates the UI to force the render thread to call
    /// SKSurface.get_Canvas() → HandleDictionary.GetOrAddObject → RWLock,
    /// while background threads hold or contend on the same lock.
    /// </summary>
    private async Task RenderInvalidationDriver(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Post to UI thread to invalidate rendering
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Force a render by changing a visual property
                    Arena.Background = new SolidColorBrush(
                        Color.FromRgb(
                            (byte)Random.Shared.Next(20, 40),
                            (byte)Random.Shared.Next(20, 40),
                            (byte)Random.Shared.Next(40, 60)));
                    Arena.InvalidateVisual();
                });

                // Don't await too long — keep the render pipeline hot
                await Task.Delay(8, ct); // ~120fps invalidation rate
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    /// <summary>
    /// Allocates memory to trigger WebAssembly.Memory.grow() which invalidates
    /// ArrayBuffer views on all threads, compounding the SkiaSharp handle issue.
    /// </summary>
    private void MemoryPressureWorker(CancellationToken ct)
    {
        var rng = new Random(99);
        var keepAlive = new List<byte[]>();

        while (!ct.IsCancellationRequested)
        {
            int size = rng.Next(1, 4) switch
            {
                1 => rng.Next(4096, 65536),
                2 => rng.Next(65536, 524288),
                _ => rng.Next(524288, 2 * 1024 * 1024),
            };

            var buf = new byte[size];
            buf[0] = 0xAB;
            buf[size - 1] = 0xFF;
            keepAlive.Add(buf);

            if (keepAlive.Count > 30)
                keepAlive.RemoveRange(0, 25);

            if (rng.Next(10) == 0)
                Thread.Sleep(1);
        }
    }

    private void AppendLog(string msg)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _log.Insert(0, msg);
        else
            Dispatcher.UIThread.Post(() => _log.Insert(0, msg));
    }
}

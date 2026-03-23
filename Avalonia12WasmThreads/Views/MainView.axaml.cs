using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Avalonia12WasmThreads.Views;

public partial class MainView : UserControl
{
    private readonly Rectangle[] _squares = new Rectangle[4];
    private readonly IBrush[] _colors = [Brushes.Coral, Brushes.DodgerBlue, Brushes.MediumSeaGreen, Brushes.Gold];
    private readonly Random _rng = new();
    private int _cycle;

    public MainView()
    {
        InitializeComponent();
        for (int i = 0; i < 4; i++)
        {
            _squares[i] = new Rectangle { Width = 60, Height = 60, Fill = _colors[i] };
            Canvas.SetLeft(_squares[i], 80 * i + 20);
            Canvas.SetTop(_squares[i], 20);
            Arena.Children.Add(_squares[i]);
        }
    }

    private async void OnRun(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RunBtn.IsEnabled = false;
        for (_cycle = 0; _cycle < 6; _cycle++)
        {
            Status.Text = $"Cycle {_cycle}: animating + network call...";

            // Pick a random square and a random target position
            var sq = _squares[_rng.Next(4)];
            double targetX = _rng.Next(20, 500), targetY = _rng.Next(20, 300);

            // Fire animation and simulated network call concurrently
            var animTask = AnimateSquare(sq, targetX, targetY);
            var netTask = FakeNetworkCall(_cycle);

            // Both must complete before the state machine advances
            try
            {
                await Task.WhenAll(animTask, netTask);
                AppendLog($"Cycle {_cycle}: net={netTask.Result}, pos=({Canvas.GetLeft(sq):F0},{Canvas.GetTop(sq):F0})");
            }
            catch (Exception ex)
            {
                AppendLog($"Cycle {_cycle}: FAILED — {ex.Message}");
            }
        }
        Status.Text = "Done — all cycles complete.";
        RunBtn.IsEnabled = true;
    }

    private async Task AnimateSquare(Rectangle sq, double toX, double toY)
    {
        var dur = TimeSpan.FromMilliseconds(300 + _rng.Next(400));

        // Animate Canvas.Left
        var xAnim = new DoubleTransition { Property = Canvas.LeftProperty, Duration = dur, Easing = new CubicEaseInOut() };
        // Animate Canvas.Top
        var yAnim = new DoubleTransition { Property = Canvas.TopProperty, Duration = dur, Easing = new CubicEaseInOut() };

        sq.Transitions = new Transitions { xAnim, yAnim };

        Canvas.SetLeft(sq, toX);
        Canvas.SetTop(sq, toY);

        // Wait for the transition to finish (approximation — transitions don't have completion events)
        await Task.Delay(dur + TimeSpan.FromMilliseconds(50));
    }

    private async Task<string> FakeNetworkCall(int cycle)
    {
        // Simulate variable network latency
        var delay = 100 + _rng.Next(600);
        await Task.Delay(delay);
        // Occasionally throw to simulate a transient failure
        if (_rng.Next(6) == 0)
            throw new Exception($"Transient network error on cycle {cycle}");
        return $"ok({delay}ms)";
    }

    private void AppendLog(string msg)
    {
        Log.Text = msg + "\n" + (Log.Text?.Length > 300 ? Log.Text[..300] : Log.Text);
    }
}

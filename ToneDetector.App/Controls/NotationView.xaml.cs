using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToneDetector.App.ViewModels;

namespace ToneDetector.App.Controls;

/// <summary>
/// Hosts a WebView2 control that renders the transcript as sheet-music notation via a locally
/// bundled VexFlow (no CDN dependency at runtime). Updates are throttled — VexFlow re-renders the
/// whole stave from scratch each call, which is too expensive to run on every single frame/note.
/// </summary>
public partial class NotationView : UserControl
{
    public static readonly DependencyProperty TranscriptProperty = DependencyProperty.Register(
        nameof(Transcript), typeof(IEnumerable<TranscriptEntryViewModel>), typeof(NotationView),
        new PropertyMetadata(null, OnTranscriptChanged));

    public IEnumerable<TranscriptEntryViewModel>? Transcript
    {
        get => (IEnumerable<TranscriptEntryViewModel>?)GetValue(TranscriptProperty);
        set => SetValue(TranscriptProperty, value);
    }

    public static readonly DependencyProperty TempoBpmProperty = DependencyProperty.Register(
        nameof(TempoBpm), typeof(double?), typeof(NotationView), new PropertyMetadata(null));

    public double? TempoBpm
    {
        get => (double?)GetValue(TempoBpmProperty);
        set => SetValue(TempoBpmProperty, value);
    }

    public static readonly DependencyProperty NotationEnabledProperty = DependencyProperty.Register(
        nameof(NotationEnabled), typeof(bool), typeof(NotationView),
        new PropertyMetadata(false, OnNotationEnabledChanged));

    public bool NotationEnabled
    {
        get => (bool)GetValue(NotationEnabledProperty);
        set => SetValue(NotationEnabledProperty, value);
    }

    public static readonly DependencyProperty ThrottleMsProperty = DependencyProperty.Register(
        nameof(ThrottleMs), typeof(int), typeof(NotationView),
        new PropertyMetadata(500, OnThrottleMsChanged));

    public int ThrottleMs
    {
        get => (int)GetValue(ThrottleMsProperty);
        set => SetValue(ThrottleMsProperty, value);
    }

    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(
        nameof(ErrorMessage), typeof(string), typeof(NotationView), new PropertyMetadata(""));

    public string ErrorMessage
    {
        get => (string)GetValue(ErrorMessageProperty);
        private set => SetValue(ErrorMessageProperty, value);
    }

    public static readonly DependencyProperty ErrorMessageVisibilityProperty = DependencyProperty.Register(
        nameof(ErrorMessageVisibility), typeof(Visibility), typeof(NotationView),
        new PropertyMetadata(Visibility.Collapsed));

    public Visibility ErrorMessageVisibility
    {
        get => (Visibility)GetValue(ErrorMessageVisibilityProperty);
        private set => SetValue(ErrorMessageVisibilityProperty, value);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly DispatcherTimer _throttleTimer;
    private bool _dirty;
    private bool _webViewReady;

    public NotationView()
    {
        InitializeComponent();

        _throttleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };
        _throttleTimer.Tick += (_, _) => FlushIfDirty();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_webViewReady)
            return;

        try
        {
            await WebView.EnsureCoreWebView2Async();
            string assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "notation.html");
            WebView.CoreWebView2.Navigate(new Uri(assetPath).AbsoluteUri);
            _webViewReady = true;

            if (NotationEnabled)
                _throttleTimer.Start();
        }
        catch (Exception ex)
        {
            // Most commonly: the WebView2 Runtime isn't installed on this machine. Fail soft —
            // notation is an optional visualization, not core functionality.
            ErrorMessage = $"Notation view unavailable: {ex.Message}";
            ErrorMessageVisibility = Visibility.Visible;
        }
    }

    private static void OnTranscriptChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NotationView)d;

        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= view.OnTranscriptCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += view.OnTranscriptCollectionChanged;

        view._dirty = true;
    }

    private void OnTranscriptCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _dirty = true;

    private static void OnNotationEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NotationView)d;
        if ((bool)e.NewValue)
        {
            view._dirty = true;
            if (view._webViewReady)
                view._throttleTimer.Start();
        }
        else
        {
            view._throttleTimer.Stop();
        }
    }

    private static void OnThrottleMsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NotationView)d;
        view._throttleTimer.Interval = TimeSpan.FromMilliseconds((int)e.NewValue);
    }

    private void FlushIfDirty()
    {
        if (!_dirty || !_webViewReady || !NotationEnabled || WebView.CoreWebView2 is null)
            return;

        _dirty = false;

        var events = (Transcript ?? Enumerable.Empty<TranscriptEntryViewModel>())
            .Select(entry => new NotationEventDto(entry.Type, entry.DurationMs, entry.Note, entry.Octave))
            .ToList();
        var payload = new NotationPayload(events, TempoBpm);

        string innerJson = JsonSerializer.Serialize(payload, JsonOptions);
        string scriptArgument = JsonSerializer.Serialize(innerJson); // re-escape as a JS string literal
        _ = WebView.CoreWebView2.ExecuteScriptAsync($"renderTranscript({scriptArgument})");
    }

    private sealed record NotationEventDto(string Type, double DurationMs, string? NoteName, int Octave);

    private sealed record NotationPayload(List<NotationEventDto> Events, double? Bpm);
}

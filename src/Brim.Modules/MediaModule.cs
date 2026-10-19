using System.Security.Cryptography;
using Brim.Core;
using Brim.Core.Widgets;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.System;

namespace Brim.Modules;

/// <summary>
/// Medien mit Cover und Steuerung (A3) über
/// <see cref="GlobalSystemMediaTransportControlsSessionManager"/>.
/// </summary>
public sealed class MediaModule : IBrimModule
{
    public const string ModuleId = "media";
    private const string ActivityId = "now";

    /// <summary>Nach so langer Pause räumt die Musik die kompakte Notch, bleibt aber im Tab „Jetzt“.</summary>
    private static readonly TimeSpan PausedToBackground = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ModuleContext? _context;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private ITimer? _ticker;

    // Zustand der aktuellen Sitzung
    private string? _title;
    private string? _artist;
    private string? _source;
    private ImageData? _cover;
    private string? _accent;
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _positionUpdated;
    private DateTimeOffset _pausedSince = DateTimeOffset.MinValue;

    public string Id => ModuleId;
    public string DisplayName => "Medien";

    public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Attach(_manager.GetCurrentSession());
        _ticker = context.Time.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Attach(sender.GetCurrentSession());

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = session;
        _cover = null;
        _accent = null;
        if (session is null)
        {
            _title = null;
            _context?.End(ActivityId);
            return;
        }
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        _source = ResolveSourceName(session.SourceAppUserModelId);
        _ = RefreshAsync(media: true);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync(media: true);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync(media: false);

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync(media: false);

    private async Task RefreshAsync(bool media)
    {
        var session = _session;
        if (session is null || _context is null)
        {
            return;
        }
        await _refreshGate.WaitAsync();
        try
        {
            if (media)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var changed = props?.Title != _title || props?.Artist != _artist;
                _title = props?.Title;
                _artist = string.IsNullOrWhiteSpace(props?.Artist) ? props?.AlbumArtist : props?.Artist;
                if (changed || _cover is null)
                {
                    (_cover, _accent) = await ReadCoverAsync(props?.Thumbnail);
                }
            }

            var playback = session.GetPlaybackInfo();
            var wasPlaying = _isPlaying;
            _isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (wasPlaying && !_isPlaying)
            {
                _pausedSince = _context.Time.GetUtcNow();
            }

            var timeline = session.GetTimelineProperties();
            if (timeline is not null)
            {
                _position = timeline.Position;
                _duration = timeline.EndTime - timeline.StartTime;
                _positionUpdated = timeline.LastUpdatedTime;
            }
            Publish();
        }
        catch (Exception ex)
        {
            // Manche Player werfen beim Wechsel der Sitzung; dann beim nächsten Ereignis erneut versuchen.
            Log.Warn("Medien konnten nicht gelesen werden", ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void Tick()
    {
        if (_title is not null && (_isPlaying || _context!.Time.GetUtcNow() - _pausedSince < PausedToBackground + TimeSpan.FromSeconds(2)))
        {
            Publish();
        }
    }

    private TimeSpan CurrentPosition()
    {
        if (!_isPlaying || _positionUpdated == default)
        {
            return _position;
        }
        var elapsed = _context!.Time.GetUtcNow() - _positionUpdated;
        var position = _position + elapsed;
        return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
    }

    private void Publish()
    {
        var context = _context!;
        if (string.IsNullOrWhiteSpace(_title))
        {
            context.End(ActivityId);
            return;
        }

        var accent = _accent ?? BrimColors.DefaultAccent;
        var position = CurrentPosition();
        var progress = _duration > TimeSpan.Zero ? position / _duration : 0;
        var subtitle = string.Join(" · ", new[] { _artist, _source }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var background = !_isPlaying && context.Time.GetUtcNow() - _pausedSince > PausedToBackground;
        (string, string) placeholder = (accent, "#6C5CFF");

        context.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = ActivityId,
            Priority = ActivityPriority.Ongoing,
            Rank = _isPlaying ? 10 : 1,
            IsBackground = background,
            Accent = accent,
            Leading = new ImageWidget(_cover) { Width = 22, Height = 22, CornerRadius = 6, Placeholder = placeholder, AccessibleName = "Cover" },
            Trailing = new LevelWidget { Active = _isPlaying, Color = accent },
            Bead = new ImageWidget(_cover) { Width = 20, Height = 20, CornerRadius = 5, Placeholder = placeholder },
            Announcement = $"{_title}, {subtitle}",
            Expanded = PlayerView(accent, placeholder, subtitle, position, progress),
        });
    }

    // ───────── Ansichten im Tab „Jetzt“ ─────────

    private Widget PlayerView(string accent, (string, string) placeholder, string subtitle, TimeSpan position, double progress) =>
        new StackWidget(
            new StackWidget(
                new ImageWidget(_cover) { Width = 92, Height = 92, CornerRadius = 16, Placeholder = placeholder, AccessibleName = "Cover" },
                new StackWidget(
                    new StackWidget(
                        new StackWidget(
                            new TextWidget(_title!) { Style = TextStyle.Title },
                            new TextWidget(subtitle) { Style = TextStyle.Secondary, FontSize = 14 })
                        {
                            Orientation = StackOrientation.Vertical,
                            Spacing = 3,
                            Align = CrossAlign.Stretch,
                            Grow = 1,
                        },
                        new LevelWidget { Active = _isPlaying, Color = accent, Height = 22 })
                    {
                        Justify = Justify.SpaceBetween,
                        Align = CrossAlign.Start,
                        Spacing = 16,
                    },
                    new StackWidget(
                        new ProgressWidget(progress) { SeekActionId = _duration > TimeSpan.Zero ? "seek" : null, Live = false, AccessibleName = "Fortschritt" },
                        new StackWidget(
                            new TextWidget(Format(position)) { Style = TextStyle.MonoCaption },
                            new TextWidget("-" + Format(_duration - position)) { Style = TextStyle.MonoCaption })
                        {
                            Justify = Justify.SpaceBetween,
                        })
                    {
                        Orientation = StackOrientation.Vertical,
                        Spacing = 7,
                        Align = CrossAlign.Stretch,
                    })
                {
                    Orientation = StackOrientation.Vertical,
                    Justify = Justify.SpaceBetween,
                    Align = CrossAlign.Stretch,
                    Grow = 1,
                })
            {
                Spacing = 18,
                Align = CrossAlign.Stretch,
            },
            new StackWidget(
                new ButtonWidget("output") { Glyph = Glyphs.Speakers, GlyphColor = BrimColors.Slate, AccessibleName = "Audioausgabe wählen" },
                new StackWidget(
                    new ButtonWidget("previous") { Glyph = Glyphs.Previous, GlyphSize = 18, AccessibleName = "Vorheriger Titel" },
                    new ButtonWidget("toggle")
                    {
                        Glyph = _isPlaying ? Glyphs.Pause : Glyphs.Play,
                        Kind = ButtonKind.Primary,
                        Size = 52,
                        AccessibleName = _isPlaying ? "Pause" : "Wiedergabe",
                    },
                    new ButtonWidget("next") { Glyph = Glyphs.Next, GlyphSize = 18, AccessibleName = "Nächster Titel" })
                {
                    Spacing = 14,
                },
                new ButtonWidget("mixer") { Glyph = Glyphs.Volume, GlyphColor = BrimColors.Slate, AccessibleName = "Lautstärke" })
            {
                Justify = Justify.SpaceBetween,
            })
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 14,
            Align = CrossAlign.Stretch,
        };

    public void OnAction(ActionInvocation invocation)
    {
        var session = _session;
        var id = invocation.ActionId;
        switch (id)
        {
            case "toggle" when session is not null:
                _ = session.TryTogglePlayPauseAsync();
                return;
            case "previous" when session is not null:
                _ = session.TrySkipPreviousAsync();
                return;
            case "next" when session is not null:
                _ = session.TrySkipNextAsync();
                return;
            case "seek" when session is not null && invocation.Value is { } v && _duration > TimeSpan.Zero:
                _ = session.TryChangePlaybackPositionAsync((long)(_duration.Ticks * Math.Clamp(v, 0, 1)));
                return;
            case "output":
                _context?.Send(AudioModule.ModuleId, AudioModule.ShowOutput);
                return;
            case "mixer":
                _context?.Send(AudioModule.ModuleId, AudioModule.ShowMixer);
                return;
        }
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static async Task<(ImageData?, string?)> ReadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return (null, null);
        }
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            // Kleine Version für die Akzentfarbe.
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform { ScaledWidth = 24, ScaledHeight = 24, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var accent = AccentColor.FromBgra(pixels.DetachPixelData());

            var id = Convert.ToHexString(SHA1.HashData(bytes));
            return (new ImageData(id, bytes), accent);
        }
        catch (Exception ex)
        {
            Log.Warn("Cover konnte nicht gelesen werden", ex);
            return (null, null);
        }
    }

    private static string? ResolveSourceName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }
        try
        {
            var info = AppInfo.GetFromAppUserModelId(appUserModelId);
            if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName))
            {
                return info.DisplayInfo.DisplayName;
            }
        }
        catch (Exception)
        {
            // Unverpackte Apps (Spotify.exe, chrome) haben keine AppInfo.
        }
        var name = appUserModelId;
        var bang = name.IndexOf('!');
        if (bang >= 0)
        {
            name = name[(bang + 1)..];
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = name.Split('.', '_')[^1];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : null;
    }

    public ValueTask DisposeAsync()
    {
        _ticker?.Dispose();
        Attach(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        return ValueTask.CompletedTask;
    }
}

using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Windows.Media.Control;

namespace Deskbar.Windows.Audio;

// PLAY, PAUSE, NEXT AND PREVIOUS for whatever Windows is playing, for the bar's now-playing line. Petre: "let's
// add audio controls, pause, next, previous", then "a" from three mockups.
//
// Windows' own media sessions (GlobalSystemMediaTransportControlsSessionManager), the ones the media panel in
// quick settings and the keyboard's media keys drive, so any player that publishes one works: YouTube Music,
// Spotify, a browser tab. Measured on his machine before this was written:
//
//   current: music.youtube.com-5929F88E_vezhnr0wkvrcy!App
//   status=Playing title='Shma Israel' artist='Alilo' next=True prev=True pause=True
//
// The CURRENT session is the one shown and controlled, the same one the media keys act on, so the bar and the
// keyboard always agree about which player they mean.
public sealed record NowPlaying(string AppId, string Title, string Artist, bool Playing, bool CanPrevious, bool CanPlayPause, bool CanNext);

public interface IMediaControl
{
    // What is playing (or paused) now, or None with no media session at all.
    Maybe<NowPlaying> Current { get; }

    // Raised (on a WinRT thread) whenever the session, its track or its play state changes.
    IObservable<Unit> Changed { get; }

    void PlayPause();
    void Next();
    void Previous();
}

public sealed class MediaControl : IMediaControl, IDisposable
{
    readonly Subject<Unit> changed = new();
    readonly Action<string>? trace;
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? watched;
    NowPlaying? current;

    // Asks Windows for the session manager in the background; until it answers, nothing is playing.
    public MediaControl(Action<string>? trace = null)
    {
        this.trace = trace;
        _ = Start();
    }

    public Maybe<NowPlaying> Current => current is { } now ? now : Maybe<NowPlaying>.None;
    public IObservable<Unit> Changed => changed.AsObservable();

    async Task Start()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => _ = Watch();
            await Watch();
        }
        catch (Exception e)
        {
            trace?.Invoke($"media: no session manager ({e.GetType().Name}: {e.Message})");
        }
    }

    // Moves the track and play-state handlers to whichever session is current, then reads it.
    async Task Watch()
    {
        var session = manager?.GetCurrentSession();
        if (!ReferenceEquals(session, watched))
        {
            if (watched is not null)
            {
                watched.MediaPropertiesChanged -= OnMediaChanged;
                watched.PlaybackInfoChanged -= OnPlaybackChanged;
            }
            watched = session;
            if (watched is not null)
            {
                watched.MediaPropertiesChanged += OnMediaChanged;
                watched.PlaybackInfoChanged += OnPlaybackChanged;
            }
        }
        await Read();
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = Read();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = Read();

    async Task Read()
    {
        try
        {
            if (watched is not { } session)
            {
                Publish(null);
                return;
            }
            var media = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            Publish(new NowPlaying(
                session.SourceAppUserModelId,
                media?.Title ?? "",
                media?.Artist ?? "",
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback.Controls.IsPreviousEnabled,
                playback.Controls.IsPlayPauseToggleEnabled || playback.Controls.IsPauseEnabled || playback.Controls.IsPlayEnabled,
                playback.Controls.IsNextEnabled));
        }
        catch (Exception e)
        {
            // A session that ends while being read: the next change reads again.
            trace?.Invoke($"media: read failed ({e.GetType().Name}: {e.Message})");
        }
    }

    void Publish(NowPlaying? now)
    {
        if (now == current) return;
        current = now;
        trace?.Invoke($"media: {now?.ToString() ?? "nothing playing"}");
        changed.OnNext(Unit.Default);
    }

    // Fire and forget: the session reports the outcome through its own change events, which is what repaints.
    public void PlayPause() => _ = watched?.TryTogglePlayPauseAsync().AsTask();
    public void Next() => _ = watched?.TrySkipNextAsync().AsTask();
    public void Previous() => _ = watched?.TrySkipPreviousAsync().AsTask();

    public void Dispose() => changed.OnCompleted();
}

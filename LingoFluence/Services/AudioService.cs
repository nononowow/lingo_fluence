using System.IO;
using System.Windows.Media;

namespace LingoFluence.Services;

/// <summary>
/// Plays audio files extracted from Anki cards using WPF MediaPlayer.
/// </summary>
public class AudioService : IDisposable
{
    private MediaPlayer _player = new();
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    public event Action? PlaybackEnded;

    public AudioService()
    {
        _player.MediaEnded += (_, _) =>
        {
            IsPlaying = false;
            PlaybackEnded?.Invoke();
        };
        _player.MediaFailed += (_, _) => IsPlaying = false;
    }

    public void Play(string filePath)
    {
        if (!File.Exists(filePath)) return;
        _player.Stop();
        _player.Open(new Uri(filePath, UriKind.Absolute));
        _player.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        _player.Stop();
        IsPlaying = false;
    }

    public void Replay() => _player.Position = TimeSpan.Zero;

    public void Dispose()
    {
        if (_disposed) return;
        _player.Close();
        _disposed = true;
    }
}

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

    // Remaining chunks of a multi-part read (see PlaySequence). Empty for single files.
    private readonly Queue<string> _queue = new();

    public bool IsPlaying { get; private set; }

    public event Action? PlaybackEnded;

    public AudioService()
    {
        _player.MediaEnded += (_, _) =>
        {
            // Chain straight into the next chunk so long text plays as one passage.
            if (_queue.Count > 0) { PlayFile(_queue.Dequeue()); return; }
            IsPlaying = false;
            PlaybackEnded?.Invoke();
        };
        _player.MediaFailed += (_, _) =>
        {
            // Skip a bad chunk rather than stalling the rest of the sequence.
            if (_queue.Count > 0) { PlayFile(_queue.Dequeue()); return; }
            IsPlaying = false;
        };
    }

    public void Play(string filePath)
    {
        if (!File.Exists(filePath)) return;
        _queue.Clear();               // a single Play cancels any queued sequence
        PlayFile(filePath);
    }

    /// <summary>
    /// Plays several files back to back — used for long text (a story paragraph) that
    /// TTS had to split into chunks, so it reads as one continuous passage.
    /// </summary>
    public void PlaySequence(IEnumerable<string> filePaths)
    {
        var files = filePaths.Where(File.Exists).ToList();
        _queue.Clear();
        if (files.Count == 0) return;
        for (int i = 1; i < files.Count; i++) _queue.Enqueue(files[i]);
        PlayFile(files[0]);
    }

    private void PlayFile(string filePath)
    {
        _player.Stop();
        _player.Open(new Uri(filePath, UriKind.Absolute));
        _player.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        _queue.Clear();
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

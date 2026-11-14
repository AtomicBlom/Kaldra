namespace Loom.Audio.Mixing;

/// <summary>
/// The thread that keeps every open <see cref="AudioStream"/>'s ring topped up, decoding a little ahead of the audio
/// thread, so neither decoding nor a file's reads ever happen on the audio thread. It wakes every 10 ms; a ring holds half
/// a second, so it can be late by most of that before a stream runs dry.
/// </summary>
internal sealed class AudioStreamer : IDisposable
{
	private readonly Thread _thread;
	private volatile AudioStream[] _streams = [];
	private volatile bool _stopping;

	public AudioStreamer()
	{
		_thread = new Thread(Run) { IsBackground = true, Name = "Loom audio streaming", Priority = ThreadPriority.AboveNormal };
		_thread.Start();
	}

	/// <summary>The streams to keep topped up: replaced whole by the game thread as streams open and close.</summary>
	public void SetStreams(AudioStream[] streams) => _streams = streams;

	public void Dispose()
	{
		_stopping = true;
		_thread.Join();
	}

	private void Run()
	{
		while (!_stopping)
		{
			foreach (var stream in _streams)
				stream.Fill(int.MaxValue);
			Thread.Sleep(10);
		}
	}
}

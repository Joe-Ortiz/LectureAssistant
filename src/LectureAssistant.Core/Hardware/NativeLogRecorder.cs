using System.Text;

namespace LectureAssistant.Core.Hardware;

/// <summary>
/// Collects a native AI runtime's log output (llama.cpp, whisper.cpp) while a model loads, so it can be parsed for
/// where the model went. Output is dropped when nothing is recording; the runtimes are otherwise chatty.
/// </summary>
public sealed class NativeLogRecorder
{
    /// <summary>Bounds memory if a recording is left running through a long job.</summary>
    private const int MaxChars = 256 * 1024;

    private readonly List<Recording> _active = [];

    /// <param name="continuation">The runtime marked this as the rest of the previous message rather than a new one.</param>
    public void Write(string? message, bool continuation = false)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (_active)
        {
            foreach (var recording in _active) recording.Append(message, continuation);
        }
    }

    public Recording Start()
    {
        var recording = new Recording(this);
        lock (_active) _active.Add(recording);
        return recording;
    }

    public sealed class Recording : IDisposable
    {
        private readonly NativeLogRecorder _owner;
        private readonly StringBuilder _text = new();

        internal Recording(NativeLogRecorder owner) => _owner = owner;

        internal void Append(string message, bool continuation)
        {
            // Messages normally end with a newline; start a new line if one didn't, unless it's continued.
            if (!continuation && _text.Length > 0 && _text[^1] != '\n') _text.Append('\n');
            if (_text.Length + message.Length <= MaxChars) _text.Append(message);
        }

        /// <summary>Stops recording and returns the non-empty lines captured.</summary>
        public IReadOnlyList<string> Stop()
        {
            Dispose();
            lock (_owner._active)
                return _text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public void Dispose()
        {
            lock (_owner._active) _owner._active.Remove(this);
        }
    }
}

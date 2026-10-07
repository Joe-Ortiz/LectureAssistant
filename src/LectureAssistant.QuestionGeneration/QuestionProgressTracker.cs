using LectureAssistant.Core;

namespace LectureAssistant.QuestionGeneration;

/// <summary>One stretch of generation: reading a transcript part (prompt processing), then writing its questions.</summary>
/// <param name="Questions">Questions this part will write.</param>
/// <param name="ReadWeight">Expected reading time, measured in "time to write one question"; 0 when there's nothing to read.</param>
internal readonly record struct PlannedPart(int Questions, double ReadWeight = 0);

/// <summary>
/// Turns generation milestones (loading the model, reading each transcript part, streamed output) into
/// <see cref="QuestionGenerationProgress"/> reports: a fraction that never goes backwards, plain-language status,
/// and a rough time left once it can be measured. Fraction-only changes are throttled; milestones are always reported.
/// </summary>
/// <remarks>
/// Work is counted in questions: each question to write is 1, and reading a part counts as the number of questions
/// that could be written in the same time. The total is fixed when the plan is known, so a wrong guess only makes the
/// bar pause or jump forward, never back. Inside the current question, progress is estimated from the output so far
/// against the average size of the questions already finished (a guess until one finishes), capped at
/// <see cref="MaxPartial"/> of a question. Thread-safe: output and the reading timer arrive on different threads.
/// </remarks>
internal sealed class QuestionProgressTracker : IDisposable
{
    internal static readonly TimeSpan MinReportInterval = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The most of an unfinished step (a question, or reading a part) the bar shows.</summary>
    internal const double MaxPartial = 0.95;

    /// <summary>Time left needs a finished question and at least this much measured time.</summary>
    private static readonly TimeSpan MinMeasuredTime = TimeSpan.FromSeconds(3);

    private readonly IProgress<QuestionGenerationProgress>? _sink;
    private readonly TimeProvider _time;
    private readonly double _guessUnitsPerQuestion;
    private readonly Lock _lock = new();

    private string _message = "";
    private bool _determinate, _complete;
    private double _shown;

    private bool _reported;
    private string? _lastMessage, _lastTimeLeft;
    private double? _lastFraction;
    private long _lastReportAt;

    private double _loadShare, _loadFraction;

    private PlannedPart[]? _parts;
    private double _planStart, _totalWeight;
    private int _totalQuestions;
    private long _planStartedAt;

    private int _part = -1;
    private double _weightBefore;
    private int _questionsBefore;
    private bool _writing;
    private double _readPartial;
    private long _readStartedAt;
    private TimeSpan _expectedRead;
    private ITimer? _timer;
    private StreamingQuestionCounter _counter = new();

    private double _unitsInQuestion, _unitsMeasured;
    private int _questionsMeasured, _questionsFinished;
    private double? _shownSecondsLeft;

    /// <param name="sink">Receives the reports; may be null.</param>
    /// <param name="guessUnitsPerQuestion">Expected output per question (tokens or characters, matching what's passed to
    /// <see cref="OnOutput"/>) until the first question finishes.</param>
    /// <param name="time">Clock and timers; the system clock when null.</param>
    public QuestionProgressTracker(IProgress<QuestionGenerationProgress>? sink, double guessUnitsPerQuestion, TimeProvider? time = null)
    {
        _sink = sink;
        _guessUnitsPerQuestion = Math.Max(1, guessUnitsPerQuestion);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A step whose length can't be known (e.g. Claude planning); shows an indeterminate bar.</summary>
    public void ReportIndeterminate(string message)
    {
        lock (_lock)
        {
            _message = message;
            _determinate = false;
            Emit(force: true);
        }
    }

    /// <summary>Changes the status text and leaves the bar where it is.</summary>
    public void Report(string message)
    {
        lock (_lock)
        {
            _message = message;
            Emit(force: true);
        }
    }

    /// <summary>Starts the model-loading step, which takes up <paramref name="share"/> (0..0.5) of the bar.</summary>
    public void BeginLoading(string message, double share)
    {
        lock (_lock)
        {
            _message = message;
            _determinate = true;
            _loadShare = double.IsFinite(share) ? Math.Clamp(share, 0, 0.5) : 0;
            _loadFraction = 0;
            Emit(force: true);
        }
    }

    /// <summary>0..1 through loading the model.</summary>
    public void ReportLoad(double fraction)
    {
        lock (_lock)
        {
            if (_parts is not null || !double.IsFinite(fraction)) return;
            _loadFraction = Math.Clamp(fraction, 0, 1);
            Emit(force: false);
        }
    }

    /// <summary>Fixes the work ahead; the rest of the bar (after loading) is shared out by weight. Follow with <see cref="BeginPart"/>.</summary>
    public void Plan(IReadOnlyList<PlannedPart> parts)
    {
        lock (_lock)
        {
            _parts = [.. parts];
            _determinate = true;
            _planStart = Math.Max(_shown, _loadShare);
            _totalWeight = Math.Max(1e-9, _parts.Sum(Weight));
            _totalQuestions = _parts.Sum(p => Math.Max(0, p.Questions));
            _planStartedAt = _time.GetTimestamp();
            // No report: the first part begins straight after and reports with its own message.
        }
    }

    /// <summary>
    /// Starts reading part <paramref name="index"/>. Until output starts, the bar creeps through the reading step by
    /// time, slowing as it nears <paramref name="expectedReadTime"/> so it doesn't run ahead.
    /// </summary>
    public void BeginPart(int index, TimeSpan expectedReadTime)
    {
        lock (_lock)
        {
            if (_parts is null || index < 0 || index >= _parts.Length) throw new ArgumentOutOfRangeException(nameof(index));
            FinishPart();
            _part = index;
            _writing = false;
            _readPartial = 0;
            _counter = new StreamingQuestionCounter();
            _unitsInQuestion = 0;
            _readStartedAt = _time.GetTimestamp();
            _expectedRead = expectedReadTime;
            if (_parts[index].ReadWeight <= 0)
            {
                // Nothing to read (e.g. Claude, whose reading happens before its text starts): straight to writing.
                _writing = true;
                _message = WritingMessage();
            }
            else
            {
                _message = _parts.Length > 1 ? $"Reading the transcript (part {index + 1} of {_parts.Length})…" : "Reading the transcript…";
                if (expectedReadTime > TimeSpan.Zero)
                    _timer = _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
            }
            Emit(force: true);
        }
    }

    /// <summary>
    /// The next piece of the current part's JSON output. <paramref name="units"/> is its size in the same unit as the
    /// per-question guess (1 per token for the local model, characters for Claude).
    /// </summary>
    public void OnOutput(ReadOnlySpan<char> piece, double units)
    {
        lock (_lock)
        {
            if (_part < 0 || _complete) return;
            if (!_writing)
            {
                _writing = true;
                StopTimer();
            }
            _unitsInQuestion += units;
            int finished = _counter.Append(piece);
            if (finished > 0)
            {
                _unitsMeasured += _unitsInQuestion;
                _questionsMeasured += finished;
                _questionsFinished += finished;
                _unitsInQuestion = 0;
            }
            var message = WritingMessage();
            bool force = finished > 0 || message != _message;
            _message = message;
            Emit(force);
        }
    }

    /// <summary>The current part is done, even if it wrote fewer questions than planned.</summary>
    public void EndPart()
    {
        lock (_lock)
        {
            FinishPart();
            Emit(force: true);
        }
    }

    /// <summary>Fills the bar.</summary>
    public void Complete(string message)
    {
        lock (_lock)
        {
            FinishPart();
            _message = message;
            _determinate = true;
            _complete = true;
            Emit(force: true);
        }
    }

    /// <summary>Advances the reading step by elapsed time; called by the timer.</summary>
    internal void Tick()
    {
        lock (_lock)
        {
            if (_part < 0 || _writing || _complete || _expectedRead <= TimeSpan.Zero) return;
            var t = _time.GetElapsedTime(_readStartedAt).TotalSeconds;
            _readPartial = Math.Min(MaxPartial, 1 - Math.Exp(-2 * t / _expectedRead.TotalSeconds));
            Emit(force: false);
        }
    }

    public void Dispose()
    {
        lock (_lock) StopTimer();
    }

    /// <summary>Calm, rounded wording: no seconds, and minutes only to the nearest whole one.</summary>
    internal static string FormatTimeLeft(double seconds) => seconds switch
    {
        < 50 => "Less than a minute left",
        < 90 => "About a minute left",
        < 90 * 60 => $"About {(int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)} minutes left",
        _ => $"About {(int)Math.Round(seconds / 3600, MidpointRounding.AwayFromZero)} hours left",
    };

    private static double Weight(PlannedPart p) => Math.Max(0, p.ReadWeight) + Math.Max(0, p.Questions);

    private double UnitsPerQuestion => _questionsMeasured > 0 ? Math.Max(1, _unitsMeasured / _questionsMeasured) : _guessUnitsPerQuestion;

    private void FinishPart()
    {
        StopTimer();
        if (_part < 0) return;
        _weightBefore += Weight(_parts![_part]);
        _questionsBefore += Math.Max(0, _parts[_part].Questions);
        _part = -1;
        _writing = false;
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private string WritingMessage()
    {
        if (_totalQuestions <= 1) return "Writing the question…";
        var questions = Math.Max(1, _parts![_part].Questions);
        var current = _questionsBefore + Math.Min(_counter.Completed + 1, questions);
        return $"Writing question {Math.Min(current, _totalQuestions)} of {_totalQuestions}…";
    }

    private double DoneWeight()
    {
        double done = _weightBefore;
        if (_part < 0) return done;
        var p = _parts![_part];
        done += Math.Max(0, p.ReadWeight) * (_writing ? 1 : _readPartial);
        if (_writing && p.Questions > 0)
        {
            int finished = Math.Min(_counter.Completed, p.Questions);
            double partial = finished < p.Questions ? Math.Min(MaxPartial, _unitsInQuestion / UnitsPerQuestion) : 0;
            done += finished + partial;
        }
        return done;
    }

    private double ComputeFraction()
    {
        if (_complete) return 1;
        if (_parts is null) return _loadShare * _loadFraction;
        return _planStart + (1 - _planStart) * Math.Min(1, DoneWeight() / _totalWeight);
    }

    private string? ComputeTimeLeft()
    {
        if (_complete || _parts is null || _questionsFinished == 0) return null;
        var elapsed = _time.GetElapsedTime(_planStartedAt);
        var done = DoneWeight();
        if (elapsed < MinMeasuredTime || done <= 0) return null;

        // Average pace so far. Only raise the shown estimate when it's clearly worse, so it doesn't flicker.
        var estimate = (_totalWeight - done) * elapsed.TotalSeconds / done;
        if (_shownSecondsLeft is not { } shown || estimate < shown || estimate > shown * 1.25 + 30)
            _shownSecondsLeft = estimate;
        return FormatTimeLeft(_shownSecondsLeft.Value);
    }

    private void Emit(bool force)
    {
        double? fraction = null;
        if (_determinate)
        {
            _shown = Math.Clamp(Math.Max(_shown, ComputeFraction()), 0, 1);
            fraction = _shown;
        }
        var timeLeft = ComputeTimeLeft();
        var now = _time.GetTimestamp();

        if (_reported)
        {
            bool textChanged = _message != _lastMessage || timeLeft != _lastTimeLeft;
            bool barChanged = fraction != _lastFraction;
            if (!textChanged && !barChanged) return;
            if (!force && !textChanged && _time.GetElapsedTime(_lastReportAt, now) < MinReportInterval) return;
        }

        _reported = true;
        _lastMessage = _message;
        _lastTimeLeft = timeLeft;
        _lastFraction = fraction;
        _lastReportAt = now;
        _sink?.Report(new QuestionGenerationProgress(_message, fraction, timeLeft));
    }
}

namespace LectureAssistant.QuestionGeneration;

/// <summary>
/// Counts finished questions in <see cref="QuestionDraftSchema"/> JSON while it's still being written, one piece at a
/// time. A question is finished when its object (root { → questions [ → item {) closes. Braces and quotes inside
/// strings are ignored, so a piece may end anywhere, even in the middle of a string or an escape.
/// </summary>
internal sealed class StreamingQuestionCounter
{
    private int _depth;
    private bool _inString, _escaped, _inQuestion;

    public int Completed { get; private set; }

    /// <summary>Scans the next piece of output; returns how many questions it finished.</summary>
    public int Append(ReadOnlySpan<char> piece)
    {
        int before = Completed;
        foreach (char c in piece)
        {
            if (_inString)
            {
                if (_escaped) _escaped = false;
                else if (c == '\\') _escaped = true;
                else if (c == '"') _inString = false;
                continue;
            }
            switch (c)
            {
                case '"': _inString = true; break;
                case '{' or '[':
                    _depth++;
                    if (c == '{' && _depth == 3) _inQuestion = true;
                    break;
                case '}' or ']':
                    if (c == '}' && _depth == 3 && _inQuestion)
                    {
                        Completed++;
                        _inQuestion = false;
                    }
                    if (_depth > 0) _depth--;
                    break;
            }
        }
        return Completed - before;
    }
}

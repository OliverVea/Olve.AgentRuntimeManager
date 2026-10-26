using System.Globalization;
using System.Text;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// A command's output, capped as it arrives: the first <c>headLength</c> characters and the last
/// <c>tailLength</c>, and a count of what fell between. Capped while reading, not afterwards, so a
/// command like <c>yes</c> costs memory only up to the cap until it times out. Thread-safe: stdout
/// and stderr append from their own readers.
/// </summary>
internal sealed class CappedOutput(int headLength = CappedOutput.DefaultHead, int tailLength = CappedOutput.DefaultTail)
{
    public const int DefaultHead = 15_000;
    public const int DefaultTail = 15_000;

    private readonly StringBuilder _head = new();
    private readonly char[] _tail = new char[tailLength];
    private int _tailStart;
    private int _tailCount;
    private long _total;

    /// <summary>How many characters were appended in all.</summary>
    public long Total
    {
        get
        {
            lock (_head)
            {
                return _total;
            }
        }
    }

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_head)
        {
            _total += text.Length;
            var room = headLength - _head.Length;
            if (room > 0)
            {
                var take = Math.Min(room, text.Length);
                // Never end the head between the halves of a surrogate pair.
                if (take < text.Length && take > 0 && char.IsHighSurrogate(text[take - 1]))
                {
                    take--;
                }

                _head.Append(text[..take]);
                text = text[take..];
                if (take < room && text.Length > 0)
                {
                    // The head is as full as it gets (a pair didn't fit): close it.
                    headLength = _head.Length;
                }
            }

            foreach (var c in text)
            {
                if (_tail.Length == 0)
                {
                    break;
                }

                if (_tailCount < _tail.Length)
                {
                    _tail[(_tailStart + _tailCount) % _tail.Length] = c;
                    _tailCount++;
                }
                else
                {
                    _tail[_tailStart] = c;
                    _tailStart = (_tailStart + 1) % _tail.Length;
                }
            }
        }
    }

    public override string ToString()
    {
        lock (_head)
        {
            var tail = new StringBuilder(_tailCount);
            for (var i = 0; i < _tailCount; i++)
            {
                tail.Append(_tail[(_tailStart + i) % _tail.Length]);
            }

            var cut = _total - _head.Length - _tailCount;
            if (cut == 0)
            {
                return _head.Append(tail).ToString();
            }

            // A tail that starts on the second half of a pair drops it.
            var tailText = tail.Length > 0 && char.IsLowSurrogate(tail[0]) ? tail.ToString(1, tail.Length - 1) : tail.ToString();
            return string.Create(CultureInfo.InvariantCulture,
                $"{_head}\n\n[... {cut + _tailCount - tailText.Length} characters of output cut here, of {_total}: only the head and the tail are kept ...]\n\n{tailText}");
        }
    }
}

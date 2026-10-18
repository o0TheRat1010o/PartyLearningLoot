namespace PartyLearningLoot.Core;

/// <summary>
/// A minimal, allocation-free scope stack for Harmony prefix/finalizer pairs. <see cref="Enter"/> returns a token that
/// remembers the previous scope; <see cref="Exit"/> restores it, so nested or re-entrant calls unwind correctly even
/// when the original method throws. Mismatched exits are counted, never thrown.
/// </summary>
public sealed class ScopeStack<T> where T : struct
{
    private T current;
    private bool hasCurrent;

    public int Depth { get; private set; }
    public int Anomalies { get; private set; }

    public readonly struct Token
    {
        public readonly T Previous;
        public readonly bool PreviousPresent;
        public readonly int Depth;
        public readonly bool Entered;

        public Token(T _previous, bool _previousPresent, int _depth)
        {
            Previous = _previous;
            PreviousPresent = _previousPresent;
            Depth = _depth;
            Entered = true;
        }
    }

    public Token Enter(T _value)
    {
        var token = new Token(current, hasCurrent, Depth + 1);
        current = _value;
        hasCurrent = true;
        Depth++;
        return token;
    }

    public void Exit(Token _token)
    {
        if (!_token.Entered)
        {
            return;
        }
        if (Depth != _token.Depth)
        {
            Anomalies++;
        }
        current = _token.Previous;
        hasCurrent = _token.PreviousPresent;
        Depth = _token.Depth - 1;
        if (Depth < 0)
        {
            Depth = 0;
            Anomalies++;
        }
    }

    public bool TryGetCurrent(out T _value)
    {
        _value = current;
        return hasCurrent;
    }

    public bool IsEmpty => !hasCurrent && Depth == 0;

    /// <summary>Forcefully clears the stack (world shutdown, reload).</summary>
    public void Reset()
    {
        current = default;
        hasCurrent = false;
        Depth = 0;
    }
}

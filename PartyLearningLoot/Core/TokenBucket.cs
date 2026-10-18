using System;
using System.Collections.Generic;

namespace PartyLearningLoot.Core;

/// <summary>Classic token bucket; time is passed in by the caller so the class stays deterministic and testable.</summary>
public sealed class TokenBucket
{
    private double tokens;
    private double lastTime;
    private bool started;

    public double Capacity { get; }
    public double RefillPerSecond { get; }

    public TokenBucket(double _capacity, double _refillPerSecond)
    {
        if (_capacity <= 0 || double.IsNaN(_capacity))
        {
            throw new ArgumentOutOfRangeException(nameof(_capacity));
        }
        if (_refillPerSecond < 0 || double.IsNaN(_refillPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(_refillPerSecond));
        }
        Capacity = _capacity;
        RefillPerSecond = _refillPerSecond;
        tokens = _capacity;
    }

    public bool TryTake(double _nowSeconds, double _cost = 1d)
    {
        Refill(_nowSeconds);
        if (tokens + 1e-9 < _cost)
        {
            return false;
        }
        tokens -= _cost;
        return true;
    }

    public double Available(double _nowSeconds)
    {
        Refill(_nowSeconds);
        return tokens;
    }

    private void Refill(double _nowSeconds)
    {
        if (!started)
        {
            started = true;
            lastTime = _nowSeconds;
            return;
        }
        double elapsed = _nowSeconds - lastTime;
        if (elapsed > 0)
        {
            tokens = Math.Min(Capacity, tokens + elapsed * RefillPerSecond);
            lastTime = _nowSeconds;
        }
    }
}

/// <summary>One token bucket per key (e.g. per reading player).</summary>
public sealed class KeyedTokenBuckets<TKey>
{
    private readonly Dictionary<TKey, TokenBucket> buckets = new Dictionary<TKey, TokenBucket>();
    private readonly double capacity;
    private readonly double refillPerSecond;

    public KeyedTokenBuckets(double _capacity, double _refillPerSecond)
    {
        capacity = _capacity;
        refillPerSecond = _refillPerSecond;
    }

    public bool TryTake(TKey _key, double _nowSeconds, double _cost = 1d)
    {
        if (!buckets.TryGetValue(_key, out TokenBucket bucket))
        {
            bucket = new TokenBucket(capacity, refillPerSecond);
            buckets.Add(_key, bucket);
        }
        return bucket.TryTake(_nowSeconds, _cost);
    }

    public void Remove(TKey _key) => buckets.Remove(_key);

    public void Clear() => buckets.Clear();
}

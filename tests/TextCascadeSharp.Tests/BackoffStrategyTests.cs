using System;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class BackoffStrategyTests
{
    private static readonly TimeSpan[] Sample =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5)
    ];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 60)]
    [InlineData(100, 60)]
    public void NormalReconnect_FollowsSpecSequences(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffStrategy.NormalReconnect.GetDelay(attempt));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 10)]
    [InlineData(100, 10)]
    public void GentleReconnect_FollowsSpecSequences(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffStrategy.GentleReconnect.GetDelay(attempt));
    }

    public static TheoryData<int, int> TransientData => new()
    {
        { 1, 2 }, { 2, 5 }, { 3, 10 }, { 4, 20 }, { 5, 30 }, { 6, 30 }
    };

    [Theory]
    [MemberData(nameof(TransientData))]
    public void SessionTransient_FollowsSpecSequences(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackoffStrategy.SessionTransient.GetDelay(attempt));
    }

    [Fact]
    public void GetDelay_FirstAttempt_UsesFirstDelay()
    {
        var strategy = new BackoffStrategy(Sample);
        Assert.Equal(Sample[0], strategy.GetDelay(1));
    }

    [Fact]
    public void GetDelay_Exhausted_UsesLastDelay()
    {
        var strategy = new BackoffStrategy(Sample);
        Assert.Equal(Sample[^1], strategy.GetDelay(Sample.Length));      // 超界取末档
        Assert.Equal(Sample[^1], strategy.GetDelay(Sample.Length + 500)); // 大幅超界仍为末档
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GetDelay_NonPositive_TreatedAsFirst(int attempt)
    {
        var strategy = new BackoffStrategy(Sample);
        Assert.Equal(Sample[0], strategy.GetDelay(attempt));
    }

    [Fact]
    public void SessionRateLimit_IsFixed30s()
    {
        var strategy = BackoffStrategy.SessionRateLimit;
        Assert.Equal(TimeSpan.FromSeconds(30), strategy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), strategy.GetDelay(10));
    }

    [Fact]
    public void Delays_IsReadOnlyMirror()
    {
        var strategy = new BackoffStrategy(Sample);
        Assert.Equal(Sample, strategy.Delays);
    }

    [Fact]
    public void EmptyDelays_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BackoffStrategy([]));
    }

    [Fact]
    public void NullDelays_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BackoffStrategy(null!));
    }
}

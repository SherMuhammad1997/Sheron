namespace Sheron.Tests;

/// <summary>
/// Tests that time something run alone, after the others: xUnit runs test classes side by side, and a busy
/// processor would make the timings meaningless.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Timing
{
    public const string Name = "Timing";
}

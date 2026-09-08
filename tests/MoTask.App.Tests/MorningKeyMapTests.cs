using System.Windows.Input;
using FluentAssertions;
using MoTask.App;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>仕様 §6 のキー割当。修飾キー付きは対象外。</summary>
public class MorningKeyMapTests
{
    [Theory]
    [InlineData(Key.T, TriageKeyAction.Register)]
    [InlineData(Key.E, TriageKeyAction.Merge)]
    [InlineData(Key.X, TriageKeyAction.Reject)]
    [InlineData(Key.L, TriageKeyAction.Postpone)]
    public void MapsTheFourKeys(Key key, TriageKeyAction expected)
        => MorningKeyMap.Resolve(key, ModifierKeys.None).Should().Be(expected);

    [Theory]
    [InlineData(Key.T, ModifierKeys.Control)]
    [InlineData(Key.E, ModifierKeys.Alt)]
    [InlineData(Key.N, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.None)]
    public void IgnoresModifiersAndOtherKeys(Key key, ModifierKeys modifiers)
        => MorningKeyMap.Resolve(key, modifiers).Should().BeNull();
}

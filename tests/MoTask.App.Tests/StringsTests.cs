using FluentAssertions;
using MoTask.App.Resources;
using Xunit;

namespace MoTask.App.Tests;

public class StringsTests
{
    [Fact]
    public void Strings_ResolveFromResx()
    {
        Strings.Brand.Should().Be("TASKS");
        string.Format(Strings.HistoryMovedFormat, "未着手", "進行中").Should().Be("未着手 → 進行中");
    }

    /// <summary>
    /// resx からキーが抜け落ちると Get はキー名をそのまま返す（フォールバック）ので、
    /// 個別のテストが1件通っているだけでは検出できない。全プロパティを走査して、
    /// 値が空でなくプロパティ名とも違う（＝実際に resx から解決できている）ことを確認する。
    /// </summary>
    [Fact]
    public void AllProperties_ResolveToNonEmptyValuesDistinctFromTheirNames()
    {
        var properties = typeof(Strings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        properties.Should().NotBeEmpty();
        foreach (var property in properties)
        {
            var value = (string?)property.GetValue(null);
            value.Should().NotBeNullOrEmpty(because: $"{property.Name} は resx に値を持つはず");
            value.Should().NotBe(property.Name, because: $"{property.Name} が resx から解決できていない（フォールバックでキー名がそのまま返っている）");
        }
    }
}

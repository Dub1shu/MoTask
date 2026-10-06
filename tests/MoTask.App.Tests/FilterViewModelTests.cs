using FluentAssertions;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>フィルタバーのラベル選択。バーに並べる選択中の要約と「選択を解除」。</summary>
public class FilterViewModelTests
{
    private static FilterViewModel WithLabels(params string[] names)
    {
        var vm = new FilterViewModel();
        vm.SetLabels(names.Select((n, i) => new Label { Id = i + 1, Name = n }));
        return vm;
    }

    private static void Select(FilterViewModel vm, params string[] names)
    {
        foreach (var l in vm.Labels.Where(l => names.Contains(l.Name))) l.IsSelected = true;
    }

    [Fact]
    public void NothingSelected_SummaryIsEmpty()
    {
        var vm = WithLabels("あ", "い");

        vm.SelectedLabelCount.Should().Be(0);
        vm.HasSelectedLabels.Should().BeFalse();
        vm.SelectedLabelsPreview.Should().BeEmpty();
        vm.SelectedLabelOverflow.Should().Be(0);
    }

    [Fact]
    public void Selecting_UpdatesCountAndPreview()
    {
        var vm = WithLabels("あ", "い", "う");

        Select(vm, "い");

        vm.SelectedLabelCount.Should().Be(1);
        vm.HasSelectedLabels.Should().BeTrue();
        vm.SelectedLabelsPreview.Select(l => l.Name).Should().Equal("い");
        vm.SelectedLabelOverflow.Should().Be(0);
    }

    [Fact]
    public void MoreThanThreeSelected_PreviewShowsFirstThreeInListOrder_AndCountsTheRest()
    {
        var vm = WithLabels("あ", "い", "う", "え", "お");

        Select(vm, "お", "い", "え", "あ");

        vm.SelectedLabelCount.Should().Be(4);
        vm.SelectedLabelsPreview.Select(l => l.Name).Should().Equal("あ", "い", "え");
        vm.SelectedLabelOverflow.Should().Be(1);
        vm.HasLabelOverflow.Should().BeTrue();
    }

    [Fact]
    public void Deselecting_ShrinksPreview()
    {
        var vm = WithLabels("あ", "い", "う", "え");
        Select(vm, "あ", "い", "う", "え");

        vm.Labels.First(l => l.Name == "あ").IsSelected = false;

        vm.SelectedLabelsPreview.Select(l => l.Name).Should().Equal("い", "う", "え");
        vm.SelectedLabelOverflow.Should().Be(0);
        vm.HasLabelOverflow.Should().BeFalse();
    }

    [Fact]
    public void ClearLabels_DeselectsAll_AndRaisesChangedOnce()
    {
        var vm = WithLabels("あ", "い", "う");
        Select(vm, "あ", "う");
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        vm.ClearLabelsCommand.Execute(null);

        vm.Labels.Should().OnlyContain(l => !l.IsSelected);
        vm.SelectedLabelCount.Should().Be(0);
        vm.SelectedLabelsPreview.Should().BeEmpty();
        vm.ToFilter().LabelIds.Should().BeEmpty();
        raised.Should().Be(1, "解除のたびに絞り込みをラベルの数だけ走らせない");
    }

    [Fact]
    public void ClearLabels_WithNothingSelected_DoesNotRaiseChanged()
    {
        var vm = WithLabels("あ");
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        vm.ClearLabelsCommand.Execute(null);

        raised.Should().Be(0);
    }

    [Fact]
    public void SetLabels_KeepsSelection_AndRebuildsSummary()
    {
        var vm = WithLabels("あ", "い");
        Select(vm, "い");

        vm.SetLabels(new[] { new Label { Id = 2, Name = "い" }, new Label { Id = 3, Name = "う" } });

        vm.SelectedLabelCount.Should().Be(1);
        vm.SelectedLabelsPreview.Select(l => l.Name).Should().Equal("い");
        vm.SelectedLabelsPreview.Single().Should().BeSameAs(vm.Labels.First(l => l.Name == "い"));
    }

    [Fact]
    public void SetLabels_DropsSelectionOfRemovedLabel_FromSummary()
    {
        var vm = WithLabels("あ", "い");
        Select(vm, "あ");

        vm.SetLabels(new[] { new Label { Id = 2, Name = "い" } });

        vm.SelectedLabelCount.Should().Be(0);
        vm.SelectedLabelsPreview.Should().BeEmpty();
    }

    [Fact]
    public void ProjectsAndLabels_FollowDisplayOrder()
    {
        var vm = new FilterViewModel();

        vm.SetProjects(new[] { new Project { Id = 1, Name = "A", Order = 1 }, new Project { Id = 2, Name = "B", Order = 0 } });
        vm.SetLabels(new[] { new Label { Id = 1, Name = "x", Order = 1 }, new Label { Id = 2, Name = "y", Order = 0 } });

        vm.Projects.Skip(1).Select(p => p.Name).Should().Equal("B", "A"); // 先頭は「すべて」
        vm.Labels.Select(l => l.Name).Should().Equal("y", "x");
    }
}

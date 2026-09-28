using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

public class GroupAutomationValueTests
{
    [Theory]
    [InlineData("", true)]              // empty = true: existing rows keep their bool behaviour
    [InlineData("  ", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void Bools(string text, bool expected) => Assert.Equal(expected, GroupAutomationValue.Parse(text));

    [Fact]
    public void Whole_number_is_an_int() => Assert.Equal(3, GroupAutomationValue.Parse("3"));

    [Fact]
    public void Decimal_is_a_float_with_either_separator()
    {
        Assert.Equal(0.5f, GroupAutomationValue.Parse("0.5"));
        Assert.Equal(0.5f, GroupAutomationValue.Parse("0,5"));   // nl-NL typing habit
    }

    [Fact]
    public void Unparseable_text_is_rejected_not_sent()
        => Assert.Null(GroupAutomationValue.Parse("banana"));

    [Theory]
    [InlineData("true", false)]
    [InlineData("7", 0)]
    public void Leaving_the_group_resets_to_the_type_default(string text, object expected)
        => Assert.Equal(expected, GroupAutomationValue.OffValue(GroupAutomationValue.Parse(text)!));

    [Fact]
    public void Float_resets_to_zero_float()
        => Assert.Equal(0f, GroupAutomationValue.OffValue(GroupAutomationValue.Parse("0.75")!));
}

using System.Windows;
using Girt.Converters;
using Xunit;

namespace Girt.Tests
{
    public class ConverterTests
    {
        [Theory]
        [InlineData(true, null, Visibility.Visible)]
        [InlineData(false, null, Visibility.Collapsed)]
        [InlineData(true, "Invert", Visibility.Collapsed)]
        [InlineData(false, "Invert", Visibility.Visible)]
        public void BoolToVisibilityConverter_RespectsConverterParameterInvert(bool input, string? parameter, Visibility expected)
        {
            var converter = new BoolToVisibilityConverter();
            var result = converter.Convert(input, typeof(Visibility), parameter, null!);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void BoolToVisibilityConverter_InvertProperty_AlsoInverts()
        {
            var converter = new BoolToVisibilityConverter { Invert = true };
            Assert.Equal(Visibility.Collapsed, converter.Convert(true, typeof(Visibility), null, null!));
            Assert.Equal(Visibility.Visible, converter.Convert(false, typeof(Visibility), null, null!));
        }
    }
}

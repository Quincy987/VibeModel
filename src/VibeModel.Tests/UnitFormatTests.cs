using VibeModel.Services.Helpers;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Every printed quantity names its unit: internal ft/ft²/ft³/rad → mm/m²/m³/°.</summary>
    public class UnitFormatTests
    {
        [Fact]
        public void Format_ConvertsInternalUnitsAndNamesTheUnit()
        {
            Assert.Equal("3000.0 mm", RevitUnitHelper.Format(QuantityKind.Length, 3000 / 304.8));
            Assert.Equal("1 m²", RevitUnitHelper.Format(QuantityKind.Area, 1 / 0.09290304));
            Assert.Equal("12.5 m²", RevitUnitHelper.Format(QuantityKind.Area, 12.5 / 0.09290304));
            Assert.Equal("2.832 m³", RevitUnitHelper.Format(QuantityKind.Volume, 100));        // 100 ft³
            Assert.Equal("45°", RevitUnitHelper.Format(QuantityKind.Angle, System.Math.PI / 4));
        }

        [Fact]
        public void Format_PureNumbersStayBare()
        {
            Assert.Equal("0.2500", RevitUnitHelper.Format(QuantityKind.Number, 0.25));
            Assert.Equal("1.5000", RevitUnitHelper.Format(QuantityKind.Unknown, 1.5));
            Assert.Null(RevitUnitHelper.Symbol(QuantityKind.Number));
            Assert.Equal("m³", RevitUnitHelper.Symbol(QuantityKind.Volume));
        }

        [Theory]
        [InlineData(QuantityKind.Length, 2750.0)]
        [InlineData(QuantityKind.Area, 18.25)]
        [InlineData(QuantityKind.Volume, 3.5)]
        [InlineData(QuantityKind.Angle, 30.0)]
        [InlineData(QuantityKind.Number, 0.7)]
        public void FromDisplay_IsTheInverseOfToDisplay(QuantityKind kind, double shown)
        {
            double internalValue = RevitUnitHelper.FromDisplay(kind, shown);
            Assert.Equal(shown, RevitUnitHelper.ToDisplay(kind, internalValue), 9);
        }

        [Fact]
        public void SpecKey_DropsTheSchemaVersion()
        {
            Assert.Equal("autodesk.spec.aec:length", RevitUnitHelper.SpecKey("autodesk.spec.aec:length-2.0.0"));
            Assert.Equal("autodesk.spec.aec:length", RevitUnitHelper.SpecKey("autodesk.spec.aec:length-1.0.0"));
            Assert.Equal("autodesk.spec:spec.number", RevitUnitHelper.SpecKey("autodesk.spec:spec.number-2.0.0"));
            Assert.Equal("", RevitUnitHelper.SpecKey(null));
            Assert.Equal("", RevitUnitHelper.SpecKey(""));
        }

        [Theory]
        [InlineData("12.5", true)]
        [InlineData("-1 234,5", true)]
        [InlineData("3000", true)]
        [InlineData("12.5 mm", false)]
        [InlineData("45.00°", false)]
        [InlineData("3' 4\"", false)]
        [InlineData("2%", false)]
        [InlineData("1:50", false)]
        [InlineData("", false)]
        [InlineData("-", false)]
        public void IsBareNumber(string text, bool bare)
        {
            Assert.Equal(bare, RevitUnitHelper.IsBareNumber(text));
        }
    }
}

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VibeModel.Services.Claude;
using VibeModel.Tests.ApiFixture;
using Xunit;

namespace VibeModel.Tests.ApiFixture
{
    // Public fixture types for the lookup tests (exported from the test assembly).
    public class Widget
    {
        public Widget() { }
        public Widget(int size) { Size = size; }
        public int Size { get; set; }
        public string Label { get; }
        public static Widget Create(int size) => new Widget(size);
        [Obsolete("Use Create")]
        public static Widget Make() => new Widget();
        public void Resize(int size) { }
        public void Resize(int width, int height) { }
        public bool TryMeasure(out double length) { length = 0; return true; }
        public event EventHandler Changed { add { } remove { } }
    }

    public sealed class SpecialWidget : Widget
    {
        public double Glow { get; set; }
    }

    public class WidgetFactory
    {
        public SpecialWidget BuildSpecial() => new SpecialWidget();
        public Widget Current { get; }
    }

    public enum Shape { Round = 1, Square = 2, [Obsolete("Use Square")] Box = 3 }
}

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 16: the reflection lookup behind `apilookup`, on the framework's own types and the
    /// fixture above (the Revit assemblies are not loadable headless; verified live).
    /// </summary>
    public class ApiLookupTests
    {
        private static readonly ApiLookup Engine = new ApiLookup(
            new[] { typeof(object).Assembly, typeof(Uri).Assembly, typeof(Widget).Assembly }, "System", "System.Text");

        [Fact]
        public void ParseArgs_NameFilterAll()
        {
            var q = ApiLookup.ParseArgs("Wall.Create --filter Cre --all");
            Assert.Equal("Wall.Create", q.Name);
            Assert.Equal("Cre", q.Filter);
            Assert.True(q.All);
            Assert.Null(q.Error);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Wall Floor")]
        [InlineData("Wall --filter")]
        public void ParseArgs_Errors(string args)
        {
            Assert.NotNull(ApiLookup.ParseArgs(args).Error);
        }

        [Fact]
        public void Type_HeaderUsingPropertiesMethodsFactories()
        {
            var r = Engine.Lookup("StringBuilder", null, false);
            Assert.True(r.Found);
            var lines = r.Text.Replace("\r\n", "\n").Split('\n');
            Assert.Equal("System.Text.StringBuilder  (sealed class)", lines[0]);
            Assert.Equal("using System.Text;   (exec imports it)", lines[1]);
            Assert.Contains("  Length : int { get; set; }", lines);
            Assert.Contains("  AppendLine() : StringBuilder", lines);
            Assert.Contains("  new StringBuilder()", lines);
            // Its own fluent methods need a StringBuilder already: no way to get one.
            Assert.DoesNotContain(Engine.Factories(typeof(StringBuilder)), l => l.Contains(".Append("));
            Assert.Contains("Methods:", lines);
            Assert.Equal("System.Text.StringBuilder", r.Data["type"]);
            Assert.Equal("class", r.Data["kind"]);
        }

        [Fact]
        public void Type_FullNameAndCaseInsensitive()
        {
            Assert.True(Engine.Lookup("system.text.stringbuilder", null, false).Found);
            Assert.True(Engine.Lookup("stringbuilder", null, false).Found);
        }

        [Fact]
        public void Type_OutsideExecImports_SaysWhatToWrite()
        {
            var r = Engine.Lookup("FileInfo", null, false);
            Assert.Contains("using System.IO;   (exec does not import it: write System.IO.FileInfo)", r.Text);
        }

        [Fact]
        public void Member_AllOverloads()
        {
            var r = Engine.Lookup("Widget.Resize", null, false);
            Assert.True(r.Found);
            Assert.Contains("  Resize(int size) : void", r.Text);
            Assert.Contains("  Resize(int width, int height) : void", r.Text);
        }

        [Fact]
        public void Member_OutParameters_AndObsolete()
        {
            Assert.Contains("TryMeasure(out double length) : bool", Engine.Lookup("Widget.TryMeasure", null, false).Text);
            var make = Engine.Lookup("Widget.Make", null, false);
            Assert.Contains("static Make() : Widget   [Obsolete: Use Create]", make.Text);
        }

        [Fact]
        public void Member_Missing_OffersClosest()
        {
            var r = Engine.Lookup("Widget.Resise", null, false);
            Assert.False(r.Found);
            Assert.Contains("has no public member 'Resise'", r.Text);
            Assert.Contains("  Widget.Resize", r.Text);
        }

        [Fact]
        public void Type_Missing_OffersClosest()
        {
            var r = Engine.Lookup("StrngBuilder", null, false);
            Assert.False(r.Found);
            Assert.Contains("System.Text.StringBuilder", r.Text);
            Assert.False((bool)r.Data["ambiguous"]);
        }

        [Fact]
        public void Type_SeveralSubstringHits_AreListedNotGuessed()
        {
            var r = Engine.Lookup("Widge", null, false);
            Assert.False(r.Found);
            Assert.True((bool)r.Data["ambiguous"]);
            var candidates = ((List<object>)r.Data["candidates"]).Cast<string>().ToList();
            Assert.Equal("VibeModel.Tests.ApiFixture.Widget", candidates[0]);
        }

        [Fact]
        public void Type_UniqueSubstring_IsFound()
        {
            Assert.Equal("VibeModel.Tests.ApiFixture.WidgetFactory", Engine.Lookup("WidgetFact", null, false).Data["type"]);
        }

        [Fact]
        public void Factories_ConstructorsStaticsElsewhereAndSubtypes()
        {
            var f = Engine.Factories(typeof(Widget));
            Assert.Equal("new Widget()", f[0]);
            Assert.Equal("new Widget(int size)", f[1]);
            Assert.Contains("Widget.Create(int size) : Widget", f);
            Assert.Contains("Widget.Make() : Widget   [Obsolete: Use Create]", f);
            // A member returning a subtype is a way to get the base type too.
            Assert.Contains("(WidgetFactory x).BuildSpecial() : SpecialWidget", f);
            Assert.Contains("(WidgetFactory x).Current : Widget", f);
            // ...but a member returning the base is no way to get the subtype.
            Assert.DoesNotContain(Engine.Factories(typeof(SpecialWidget)), l => l.Contains(".Current"));
        }

        [Fact]
        public void Enum_ValuesAndObsolete()
        {
            var r = Engine.Lookup("Shape", null, false);
            Assert.Contains("  Round = 1", r.Text);
            Assert.Contains("  Box = 3   [Obsolete: Use Square]", r.Text);
            Assert.Equal("enum", r.Data["kind"]);
        }

        [Fact]
        public void Filter_NarrowsMembers()
        {
            var r = Engine.Lookup("StringBuilder", "Insert", false);
            Assert.Contains("Insert(", r.Text);
            Assert.DoesNotContain("AppendLine", r.Text.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("  ") && !l.Contains("new ")));
        }

        [Fact]
        public void DeclaredOnly_UnlessAll()
        {
            var special = Engine.Lookup("SpecialWidget", null, false).Text;
            Assert.Contains("Glow : double", special);
            Assert.DoesNotContain("Size : int", special);
            var all = Engine.Lookup("SpecialWidget", null, true).Text;
            Assert.Contains("Size : int", all);
            Assert.DoesNotContain("GetHashCode", all);
        }

        [Fact]
        public void ManyMembers_AreCut_WithAFilterHint()
        {
            var r = Engine.Lookup("Convert", null, false);
            var lines = r.Text.Replace("\r\n", "\n").Split('\n');
            Assert.Contains(lines, l => l.StartsWith("(+") && l.EndsWith("more — --filter <x>)"));
            Assert.True((int)r.Data["more"] > 0);
        }

        [Fact]
        public void Events_AreListed()
        {
            Assert.Contains("Changed : EventHandler", Engine.Lookup("Widget", null, false).Text);
        }

        [Theory]
        [InlineData(typeof(List<int>), "List<int>")]
        [InlineData(typeof(int[]), "int[]")]
        [InlineData(typeof(double?), "double?")]
        [InlineData(typeof(Dictionary<string, List<Widget>>), "Dictionary<string, List<Widget>>")]
        [InlineData(typeof(Environment.SpecialFolder), "Environment.SpecialFolder")]
        public void TypeNames_ReadLikeCSharp(Type type, string expected)
        {
            Assert.Equal(expected, ApiLookup.TypeName(type, false));
        }

        [Fact]
        public void CompileErrors_MapToLookups_EnglishAndDutch()
        {
            var errors = new[]
            {
                new ApiLookup.CompileError { Number = "CS1061", Column = 9, Line = "var x = w.Foo;",
                    Message = "'Autodesk.Revit.DB.Wall' does not contain a definition for 'Foo' and no extension method 'Foo' accepting a first argument of type 'Autodesk.Revit.DB.Wall' could be found (are you missing a using directive or an assembly reference?)" },
                // Measured on this PC (Dutch Windows): no quotes at all.
                new ApiLookup.CompileError { Number = "CS1061", Column = 21, Line = "    sb.AppendLine(w.Widht.ToString());",
                    Message = "Autodesk.Revit.DB.Wall bevat geen definitie voor Widht en er is geen extensiemethode Widht gevonden waarmee het eerste argument van het type Autodesk.Revit.DB.Wall wordt geaccepteerd (mogelijk ontbreekt er een gebruiksinstructie of een assembly-verwijzing)" },
                new ApiLookup.CompileError { Number = "CS0246", Column = 50, Line = "var c = new FilteredElementCollector(doc).OfClass(typeof(Waal)).Count();",
                    Message = "Het type of de naamruimtenaam Waal is niet gevonden (ontbreekt er een gebruiksinstructie of assembly-verwijzing?)" },
                new ApiLookup.CompileError { Number = "CS1061", Column = 1, Line = "list.Bar();",
                    Message = "'System.Collections.Generic.List<Autodesk.Revit.DB.Wall>' does not contain a definition for 'Bar'" },
                new ApiLookup.CompileError { Number = "CS1002", Column = 1, Line = "x", Message = "; expected" },
                new ApiLookup.CompileError { Number = "CS1061", Column = 9, Line = "var x = w.Foo;", Message = "duplicate: 'Autodesk.Revit.DB.Wall' does not contain a definition for 'Foo'" },
            };
            Assert.Equal(new[] { "Wall.Foo", "Wall.Widht", "Waal" }, ApiLookup.LookupsForCompileErrors(errors).ToArray());
        }

        /// <summary>The real compiler exec uses (CodeDom), in whatever language this Windows speaks.</summary>
        [Fact]
        public void CompileErrors_FromTheRealCompiler()
        {
            const string source = "public static class T {\n public static object M(System.Text.StringBuilder w) {\n  var a = w.Lenght;\n  return typeof(Strng);\n }\n}";
            CompilerResults results;
            using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
                results = provider.CompileAssemblyFromSource(new CompilerParameters { GenerateInMemory = true }, source);
            var lines = source.Split('\n');
            var errors = results.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => new ApiLookup.CompileError
            {
                Number = e.ErrorNumber, Message = e.ErrorText, Column = e.Column,
                Line = e.Line >= 1 && e.Line <= lines.Length ? lines[e.Line - 1] : null
            }).ToList();
            Assert.Equal(new[] { "StringBuilder.Lenght", "Strng" }, ApiLookup.LookupsForCompileErrors(errors).ToArray());
        }

        [Fact]
        public void EditDistance_StopsEarly()
        {
            Assert.Equal(1, ApiLookup.EditDistance("wal", "wall", 2));
            Assert.Equal(3, ApiLookup.EditDistance("a", "abcdef", 2));
        }

        [Fact]
        public void Closest_PrefixFirst_ThenDistance()
        {
            var c = ApiLookup.Closest("Wal", new[] { "Floor", "WallType", "Wall", "Wol", "SweptWall" });
            Assert.Equal(new[] { "Wall", "WallType", "Wol", "SweptWall" }, c.ToArray());
        }
    }
}

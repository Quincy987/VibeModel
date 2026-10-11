using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>checks.json parsing, report rendering (OK/WARN/FAIL lines, Fix, Verdict), the post-command line and the hit log.</summary>
    public class CheckRulesTests
    {
        [Fact]
        public void ExampleChecks_ParseIntoKnownKinds()
        {
            var path = Path.Combine(StandardsPackTests.ExamplePackFolder(), "checks.json");
            var rules = CheckRule.ParseAll(File.ReadAllText(path), out var error);
            Assert.Null(error);
            Assert.Equal(13, rules.Count);
            Assert.All(rules, r => Assert.Contains(r.Kind, ModelChecks.KnownKinds));

            var pf = rules.Single(r => r.Id == "param-filled");
            Assert.Equal(CheckSeverity.Warn, pf.Severity);
            Assert.Equal(new[] { "EX_ObjectCode", "EX_ObjectDescription" }, pf.GetStrings("parameters").ToArray());
            Assert.Equal("new", pf.GetString("phaseKey"));
            Assert.Equal(CheckSeverity.Fail, rules.Single(r => r.Id == "phase-not-none").Severity);
        }

        [Fact]
        public void BareArray_KindDefaultsToId_BadEntriesSkipped()
        {
            var rules = CheckRule.ParseAll("[{\"id\":\"pinned\"},{\"kind\":\"x\"},{\"id\":\"a\",\"kind\":\"file-name\",\"severity\":\"error\",\"enabled\":false}]", out var error);
            Assert.Null(error);
            Assert.Equal(2, rules.Count);
            Assert.Equal("pinned", rules[0].Kind);
            Assert.Equal(CheckSeverity.Fail, rules[1].Severity);
            Assert.False(rules[1].Enabled);
        }

        [Fact]
        public void InvalidJson_ReportsError()
        {
            var rules = CheckRule.ParseAll("{nope", out var error);
            Assert.Empty(rules);
            Assert.NotNull(error);
        }

        private static List<CheckFinding> SampleFindings()
        {
            var warn = new CheckRule { Id = "param-filled", Kind = "param-filled", Severity = CheckSeverity.Warn, Message = "Object code", Fix = "Fill it." };
            var fail = new CheckRule { Id = "survey-point-origin", Kind = "survey-point-origin", Severity = CheckSeverity.Fail, Fix = "Put it back." };
            var ok = new CheckRule { Id = "links-are-links", Kind = "links-are-links" };
            var skip = new CheckRule { Id = "file-name", Kind = "file-name" };
            return new List<CheckFinding>
            {
                CheckFinding.Problem(warn, "2 elements empty", Enumerable.Range(1, 20)),
                CheckFinding.Problem(fail, "moved 5 mm"),
                CheckFinding.Ok(ok, "no imports"),
                CheckFinding.Skip(skip, "not saved")
            };
        }

        [Fact]
        public void Render_HasRuleLinesFixesAndVerdict()
        {
            var text = CheckReport.Render("CHECK — test", SampleFindings());
            Assert.Contains("WARN:    param-filled — Object code — 2 elements empty [ids: 1, 2", text);
            Assert.Contains("+8 more]", text);
            Assert.Contains("Fix: Fill it.", text);
            Assert.Contains("FAIL:    survey-point-origin", text);
            Assert.Contains("OK       links-are-links", text);
            Assert.Contains("SKIPPED: file-name — not saved", text);
            Assert.Contains("Verdict: 1 FAIL, 1 WARN, 1 OK, 1 skipped — start with survey-point-origin.", text);
        }

        [Fact]
        public void Verdict_Clean()
        {
            var ok = new CheckRule { Id = "a", Kind = "a" };
            Assert.StartsWith("Verdict: clean", CheckReport.Verdict(new List<CheckFinding> { CheckFinding.Ok(ok, "fine") }));
        }

        [Fact]
        public void SummaryLine_OnlyWhenProblems()
        {
            Assert.Null(CheckReport.SummaryLine(SampleFindings().Where(f => !f.IsProblem).ToList()));
            Assert.Equal("-- check: 1 FAIL, 1 WARN (param-filled ×20, survey-point-origin) → run 'check' for fixes",
                CheckReport.SummaryLine(SampleFindings()));
        }

        [Fact]
        public void Json_HasRuleSeverityAndIds()
        {
            var json = (List<object>)CheckReport.ToJson(SampleFindings());
            var first = (Dictionary<string, object>)json[0];
            Assert.Equal("param-filled", first["rule"]);
            Assert.Equal("warn", first["severity"]);
            Assert.Equal(20, ((List<object>)first["ids"]).Count);
        }

        [Fact]
        public void CheckLog_AppendsProblemsOnly_AndAggregates()
        {
            var path = Path.Combine(Path.GetTempPath(), "vm-checklog-" + Guid.NewGuid().ToString("N"), "log.jsonl");
            try
            {
                CheckLog.Append(path, "p", "check", SampleFindings());
                CheckLog.Append(path, "p", "post-command", SampleFindings().Take(1));
                var stats = CheckLog.Stats(path);
                Assert.Equal("param-filled", stats[0].Item1);
                Assert.Equal(2, stats[0].Item2);
                Assert.Equal(40, stats[0].Item3);
                Assert.Equal(2, stats.Count); // OK and SKIPPED are never logged
                Assert.Empty(CheckLog.Stats(path + ".absent"));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { }
            }
        }
    }
}

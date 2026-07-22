using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.SourceBrowser.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.SourceBrowser.HtmlGenerator.Tests
{
    /// <summary>
    /// Covers the pure decision that used to live inside
    /// <c>SolutionGenerator.GenerateAsync</c>: when several projects (typically coming from
    /// different binlogs) share the same assembly short name, do we skip the later ones (the old
    /// "first one wins" behavior) or relocate them into unique folders?
    /// </summary>
    [TestClass]
    public class AssemblyNameDeduplicatorTests
    {
        private static HashSet<string> NewSet() =>
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [TestMethod]
        public void FirstOccurrence_IsIndexedAsIs()
        {
            var reserved = NewSet();

            var resolved = AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: false);

            // Common case: identical string returned so no override is ever created and the
            // output layout matches the pre-feature behavior exactly.
            Assert.AreEqual("Foo", resolved);
            CollectionAssert.AreEquivalent(new[] { "Foo" }, reserved.ToArray());
        }

        [TestMethod]
        public void Duplicate_WhenNotAllowed_ReturnsNullToSignalSkip()
        {
            var reserved = NewSet();
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: false);

            var resolved = AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: false);

            Assert.IsNull(resolved, "Without /allowduplicateassemblies the second copy must be skipped.");
            // The reserved set must not have gained anything spurious.
            CollectionAssert.AreEquivalent(new[] { "Foo" }, reserved.ToArray());
        }

        [TestMethod]
        public void Duplicate_WhenAllowed_IsRelocatedToSuffix_2()
        {
            var reserved = NewSet();
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            var resolved = AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            Assert.AreEqual("Foo_2", resolved);
            CollectionAssert.AreEquivalent(new[] { "Foo", "Foo_2" }, reserved.ToArray());
        }

        [TestMethod]
        public void ThirdDuplicate_GetsSuffix_3()
        {
            var reserved = NewSet();
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            var resolved = AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            Assert.AreEqual("Foo_3", resolved);
        }

        [TestMethod]
        public void SuffixCollision_SkipsToNextFreeIndex()
        {
            var reserved = NewSet();
            // Simulate a real project named "Foo_2" being indexed first, then a duplicate "Foo".
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);
            AssemblyNameDeduplicator.Resolve("Foo_2", reserved, allowDuplicates: true);

            var resolved = AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            Assert.AreEqual("Foo_3", resolved, "Must not clobber the real project already indexed as Foo_2.");
        }

        [TestMethod]
        public void DistinctAssemblies_AreAllReservedAsIs()
        {
            var reserved = NewSet();

            var a = AssemblyNameDeduplicator.Resolve("Alpha", reserved, allowDuplicates: true);
            var b = AssemblyNameDeduplicator.Resolve("Beta", reserved, allowDuplicates: true);
            var c = AssemblyNameDeduplicator.Resolve("Gamma", reserved, allowDuplicates: false);

            Assert.AreEqual("Alpha", a);
            Assert.AreEqual("Beta", b);
            Assert.AreEqual("Gamma", c);
        }

        [TestMethod]
        public void Comparison_IsCaseInsensitive_WhenSetIs()
        {
            // Program.cs builds the shared processed set with OrdinalIgnoreCase; pin that behavior.
            var reserved = NewSet();
            AssemblyNameDeduplicator.Resolve("Foo", reserved, allowDuplicates: true);

            var resolved = AssemblyNameDeduplicator.Resolve("foo", reserved, allowDuplicates: true);

            Assert.AreEqual("foo_2", resolved);
        }

        [TestMethod]
        public void GetUniqueName_ReservesTheReturnedName()
        {
            var reserved = NewSet();
            reserved.Add("Foo");

            var unique = AssemblyNameDeduplicator.GetUniqueName("Foo", reserved);

            Assert.AreEqual("Foo_2", unique);
            Assert.IsTrue(reserved.Contains("Foo_2"));
        }

        [TestMethod]
        public void Resolve_NullAssemblyName_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => AssemblyNameDeduplicator.Resolve(null, NewSet(), allowDuplicates: true));
        }

        [TestMethod]
        public void Resolve_NullReservedSet_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => AssemblyNameDeduplicator.Resolve("Foo", null, allowDuplicates: true));
        }

        /// <summary>
        /// End-to-end simulation of the exact scenario in the bug report: two binlogs each with
        /// projects "Alpha" and "Shared". With the flag off we lose one "Shared". With the flag on
        /// we keep both, relocated so folders don't collide.
        /// </summary>
        [TestMethod]
        public void TwoBinlogsSharingAnAssembly_KeepBothWhenAllowed()
        {
            var binlog1 = new[] { "Alpha", "Shared" };
            var binlog2 = new[] { "Beta", "Shared" };

            var withoutFlag = SimulateRun(new[] { binlog1, binlog2 }, allowDuplicates: false);
            CollectionAssert.AreEquivalent(
                new[] { "Alpha", "Shared", "Beta" },
                withoutFlag,
                "Legacy behavior: the second 'Shared' is silently dropped.");

            var withFlag = SimulateRun(new[] { binlog1, binlog2 }, allowDuplicates: true);
            CollectionAssert.AreEquivalent(
                new[] { "Alpha", "Shared", "Beta", "Shared_2" },
                withFlag,
                "With /allowduplicateassemblies the second 'Shared' is kept under 'Shared_2'.");
        }

        /// <summary>
        /// Lightweight benchmark: 10,000 unique assemblies + 100 duplicates should be resolved in
        /// well under half a second on any modern machine. This exists to catch accidental
        /// quadratic regressions in the disambiguation loop, not to measure absolute speed, so the
        /// bound is deliberately loose.
        /// </summary>
        [TestMethod]
        public void Benchmark_LargeInputCompletesQuickly()
        {
            const int uniqueCount = 10_000;
            const int duplicateCount = 100;

            var reserved = NewSet();
            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < uniqueCount; i++)
            {
                var name = AssemblyNameDeduplicator.Resolve("Asm" + i, reserved, allowDuplicates: true);
                Assert.AreEqual("Asm" + i, name);
            }

            // Now hammer the same name to exercise the GetUniqueName loop.
            for (int i = 0; i < duplicateCount; i++)
            {
                var name = AssemblyNameDeduplicator.Resolve("Asm0", reserved, allowDuplicates: true);
                Assert.IsNotNull(name);
                Assert.AreNotEqual("Asm0", name);
            }

            stopwatch.Stop();

            Assert.AreEqual(uniqueCount + duplicateCount, reserved.Count);
            Assert.IsTrue(
                stopwatch.ElapsedMilliseconds < 500,
                $"Resolving {uniqueCount} unique + {duplicateCount} duplicate names took {stopwatch.ElapsedMilliseconds} ms, which is way above the sanity bound.");
        }

        /// <summary>
        /// Benchmark check for the "no duplicates" hot path: the change must not add measurable
        /// overhead when nothing collides. Compares two runs on the same input.
        /// </summary>
        [TestMethod]
        public void Benchmark_AllUniqueInput_HasNoMeasurableOverheadVsPlainSetAdd()
        {
            const int n = 20_000;
            var names = Enumerable.Range(0, n).Select(i => "Asm" + i).ToArray();

            // Baseline: what the code did before the helper existed (just adding to a HashSet).
            var baselineSet = NewSet();
            var baseline = Stopwatch.StartNew();
            foreach (var name in names)
            {
                baselineSet.Add(name);
            }

            baseline.Stop();

            // Helper path: goes through Resolve, which for unique names is just Add + return.
            var helperSet = NewSet();
            var helper = Stopwatch.StartNew();
            foreach (var name in names)
            {
                AssemblyNameDeduplicator.Resolve(name, helperSet, allowDuplicates: true);
            }

            helper.Stop();

            // Allow generous slack: helper does an extra method call and null-check per name.
            // We only want to catch a runaway regression (e.g. accidental O(n^2)).
            var slackMs = Math.Max(50, baseline.ElapsedMilliseconds * 10);
            Assert.IsTrue(
                helper.ElapsedMilliseconds <= baseline.ElapsedMilliseconds + slackMs,
                $"Helper took {helper.ElapsedMilliseconds} ms vs baseline {baseline.ElapsedMilliseconds} ms for {n} unique names.");
        }

        /// <summary>
        /// Runs the same decision that <c>SolutionGenerator.GenerateAsync</c> makes for every
        /// project across every binlog in a run, and returns the flat list of names that would end
        /// up being indexed.
        /// </summary>
        private static List<string> SimulateRun(IEnumerable<string[]> binlogs, bool allowDuplicates)
        {
            var reserved = NewSet();
            var indexed = new List<string>();

            foreach (var binlog in binlogs)
            {
                foreach (var assemblyName in binlog)
                {
                    var resolved = AssemblyNameDeduplicator.Resolve(assemblyName, reserved, allowDuplicates);
                    if (resolved != null)
                    {
                        indexed.Add(resolved);
                    }
                }
            }

            return indexed;
        }
    }
}

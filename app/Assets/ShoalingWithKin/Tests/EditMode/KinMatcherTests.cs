using System.Collections.Generic;
using NUnit.Framework;

namespace ShoalingWithKin.Tests
{
    public class KinMatcherTests
    {
        static Dictionary<string, float> P(float rock, float tree, float other = 0f)
        {
            float nothing = System.Math.Max(0f, 1f - rock - tree - other);
            return new Dictionary<string, float> { ["rock"] = rock, ["tree"] = tree, ["other"] = other, ["nothing"] = nothing };
        }

        static KinMatcher Matcher(int enter = 1, int exit = 2)
        {
            var settings = KinMatcherSettings.Default;
            settings.EnterCount = enter;
            settings.ExitCount = exit;
            return new KinMatcher(new[] { "rock", "tree" }, settings);
        }

        [Test]
        public void MatchesOnTheFirstConfidentFrame()
        {
            var m = Matcher();
            var change = m.Observe(P(0.97f, 0f), 1);
            Assert.AreEqual("rock", change.Matched);
            Assert.IsNull(change.Released);
            Assert.AreEqual("rock", m.Matched);
        }

        [Test]
        public void HoveringBelowTheThresholdDoesNotMatch()
        {
            var m = Matcher();
            Assert.IsFalse(m.Observe(P(0.17f, 0.02f), 1).Any);
            Assert.IsFalse(m.Observe(P(0.45f, 0f), 2).Any);
            Assert.IsNull(m.Matched);
            Assert.AreEqual("rock", m.Leading);
        }

        [Test]
        public void EnterCountNeedsConsecutiveFrames()
        {
            var m = Matcher(enter: 2);
            Assert.IsFalse(m.Observe(P(0.9f, 0f), 1).Any);
            Assert.IsFalse(m.Observe(P(0.1f, 0f), 2).Any, "a dip resets the streak");
            Assert.IsFalse(m.Observe(P(0.9f, 0f), 3).Any);
            Assert.AreEqual("rock", m.Observe(P(0.9f, 0f), 4).Matched);
        }

        [Test]
        public void HoldsThroughOneMissedFrameAndReleasesAfterTwo()
        {
            var m = Matcher();
            m.Observe(P(1f, 0f), 1);
            Assert.IsFalse(m.Observe(P(0.1f, 0f), 2).Any);
            Assert.AreEqual("rock", m.Matched);
            Assert.IsFalse(m.Observe(P(0.8f, 0f), 3).Any, "back above exit resets the exit streak");
            Assert.IsFalse(m.Observe(P(0.2f, 0f), 4).Any);
            var change = m.Observe(P(0.2f, 0f), 5);
            Assert.AreEqual("rock", change.Released);
            Assert.IsNull(m.Matched);
        }

        [Test]
        public void ValuesBetweenExitAndEnterKeepTheMatch()
        {
            var m = Matcher();
            m.Observe(P(1f, 0f), 1);
            for (int i = 2; i < 6; i++)
                Assert.IsFalse(m.Observe(P(0.4f, 0f), i).Any);
            Assert.AreEqual("rock", m.Matched);
        }

        [Test]
        public void SwitchesStraightFromRockToTree()
        {
            var m = Matcher();
            m.Observe(P(1f, 0f), 1);
            var change = m.Observe(P(0f, 0.95f), 2);
            Assert.AreEqual("rock", change.Released);
            Assert.AreEqual("tree", change.Matched);
            Assert.AreEqual("tree", m.Matched);
        }

        [Test]
        public void OtherAndNothingNeverMatch()
        {
            var m = Matcher();
            Assert.IsFalse(m.Observe(P(0f, 0f, other: 1f), 1).Any);
            Assert.IsFalse(m.Observe(P(0f, 0f), 2).Any);
            Assert.IsNull(m.Matched);
        }

        [Test]
        public void OutOfOrderAnswersAreDropped()
        {
            var m = Matcher();
            m.Observe(P(0f, 0f), 5);
            var change = m.Observe(P(1f, 0f), 4);
            Assert.IsTrue(change.Stale);
            Assert.IsNull(m.Matched);
        }

        [Test]
        public void SmoothedEasesTowardTheLatestAnswer()
        {
            var m = Matcher();
            m.Observe(P(0f, 0f), 1);
            m.Observe(P(1f, 0f), 2);
            float eased = m.Smoothed["rock"];
            Assert.That(eased, Is.GreaterThan(0.4f).And.LessThan(1f));
        }

        [Test]
        public void ForceReleaseEndsTheMatchButStillDropsOlderAnswers()
        {
            var m = Matcher();
            m.Observe(P(1f, 0f), 3);
            var change = m.ForceRelease();
            Assert.AreEqual("rock", change.Released);
            Assert.IsNull(m.Matched);
            Assert.IsTrue(m.Observe(P(1f, 0f), 2).Stale, "an answer for an older frame still in flight");
            Assert.AreEqual("rock", m.Observe(P(1f, 0f), 4).Matched, "a fresh answer can match again");
        }

        [Test]
        public void ForceReleaseWithoutAMatchChangesNothing()
        {
            Assert.IsFalse(Matcher().ForceRelease().Any);
        }

        [Test]
        public void AnOutcomeMissingFromAnAnswerEasesTowardZero()
        {
            var m = Matcher();
            m.Observe(P(0f, 1f), 1);
            m.Observe(new Dictionary<string, float> { ["rock"] = 1f }, 2);
            Assert.That(m.Smoothed["tree"], Is.LessThan(1f));
        }
    }
}

using System;
using System.Collections.Generic;

namespace ShoalingWithKin
{
    /// <summary>What one observation changed: a kin released, a kin matched, both (a switch), or neither.</summary>
    public readonly struct MatchChange
    {
        public readonly string Released;
        public readonly string Matched;
        public readonly bool Stale;

        public MatchChange(string released, string matched, bool stale = false)
        {
            Released = released;
            Matched = matched;
            Stale = stale;
        }

        public bool Any => Released != null || Matched != null;
    }

    [Serializable]
    public struct KinMatcherSettings
    {
        public float EnterThreshold;
        public float ExitThreshold;
        public int EnterCount;
        public int ExitCount;
        public float DisplaySmoothing;

        /// <summary>
        /// Touches score ~1.0 and hovers ≤ 0.17 on the 512 px test set, so 0.6 / 0.35 leaves room on both sides.
        /// One frame to enter keeps the response near the request latency; two to exit rides out one missed frame.
        /// </summary>
        public static KinMatcherSettings Default => new()
        {
            EnterThreshold = 0.6f,
            ExitThreshold = 0.35f,
            EnterCount = 1,
            ExitCount = 2,
            DisplaySmoothing = 0.4f,
        };
    }

    /// <summary>
    /// Turns a stream of touch distributions into "matched with this kin" with hysteresis.
    /// Requests overlap, so answers can arrive out of order: each carries its frame's sequence
    /// number and older ones are dropped.
    /// </summary>
    public sealed class KinMatcher
    {
        readonly HashSet<string> m_Kin;
        readonly Dictionary<string, float> m_Smoothed = new();
        readonly HashSet<string> m_Keys = new();
        KinMatcherSettings m_Settings;
        long m_LastSequence = -1;
        string m_Pending;
        int m_PendingStreak;
        int m_ExitStreak;

        public KinMatcher(IEnumerable<string> kinIds, KinMatcherSettings settings)
        {
            m_Kin = new HashSet<string>(kinIds);
            m_Settings = settings;
        }

        /// <summary>The kin the hand is matched with, or null.</summary>
        public string Matched { get; private set; }

        /// <summary>Probabilities eased for display (the match itself uses the raw values).</summary>
        public IReadOnlyDictionary<string, float> Smoothed => m_Smoothed;

        /// <summary>The highest raw kin probability in the latest observation.</summary>
        public float LeadingProbability { get; private set; }

        /// <summary>The kin with the highest raw probability in the latest observation.</summary>
        public string Leading { get; private set; }

        public KinMatcherSettings Settings
        {
            get => m_Settings;
            set => m_Settings = value;
        }

        public MatchChange Observe(IReadOnlyDictionary<string, float> probabilities, long sequence)
        {
            if (sequence <= m_LastSequence)
                return new MatchChange(null, null, stale: true);
            m_LastSequence = sequence;

            float keep = Math.Clamp(m_Settings.DisplaySmoothing, 0f, 0.95f);
            m_Keys.Clear();
            m_Keys.UnionWith(m_Smoothed.Keys);
            m_Keys.UnionWith(probabilities.Keys);
            // An outcome missing from this answer eases toward 0 rather than freezing.
            foreach (var key in m_Keys)
            {
                float p = Get(probabilities, key);
                m_Smoothed[key] = m_Smoothed.TryGetValue(key, out var old) ? old * keep + p * (1f - keep) : p;
            }

            Leading = null;
            LeadingProbability = 0f;
            foreach (var id in m_Kin)
            {
                float p = Get(probabilities, id);
                if (p > LeadingProbability)
                {
                    Leading = id;
                    LeadingProbability = p;
                }
            }

            // A different kin over the enter threshold builds a streak, matched or not.
            bool candidate = Leading != null && Leading != Matched && LeadingProbability >= m_Settings.EnterThreshold;
            if (candidate)
            {
                m_PendingStreak = Leading == m_Pending ? m_PendingStreak + 1 : 1;
                m_Pending = Leading;
            }
            else
            {
                m_Pending = null;
                m_PendingStreak = 0;
            }

            if (Matched != null)
                m_ExitStreak = Get(probabilities, Matched) < m_Settings.ExitThreshold ? m_ExitStreak + 1 : 0;

            bool enter = m_Pending != null && m_PendingStreak >= Math.Max(1, m_Settings.EnterCount);
            bool exit = Matched != null && (enter || m_ExitStreak >= Math.Max(1, m_Settings.ExitCount));

            string released = null, matched = null;
            if (exit)
            {
                released = Matched;
                Matched = null;
                m_ExitStreak = 0;
            }
            if (enter)
            {
                matched = m_Pending;
                Matched = m_Pending;
                m_Pending = null;
                m_PendingStreak = 0;
                m_ExitStreak = 0;
            }
            return new MatchChange(released, matched);
        }

        /// <summary>
        /// Ends the match without an answer (answers stopped arriving, or detection paused). Keeps the
        /// last sequence number, so answers already in flight for older frames are still dropped.
        /// </summary>
        public MatchChange ForceRelease()
        {
            var released = Matched;
            Matched = null;
            m_Pending = null;
            m_PendingStreak = 0;
            m_ExitStreak = 0;
            return new MatchChange(released, null);
        }

        public void Reset()
        {
            Matched = null;
            Leading = null;
            LeadingProbability = 0f;
            m_Pending = null;
            m_PendingStreak = 0;
            m_ExitStreak = 0;
            m_LastSequence = -1;
            m_Smoothed.Clear();
        }

        static float Get(IReadOnlyDictionary<string, float> d, string id) => d.TryGetValue(id, out var p) ? p : 0f;
    }
}

using System;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>How the wearer picks a kin.</summary>
    public enum KinMode
    {
        /// <summary>Skin on the kin: rock or tree, at the creek.</summary>
        Touch,
        /// <summary>The index finger aimed at the kin (or the hand on it): rock, table or chair, indoors too.</summary>
        Point,
    }

    /// <summary>How the shoal makes camp with a kin once the hand touches it.</summary>
    public enum ShoalGesture
    {
        /// <summary>The fish converge on the touch point and slip into it, one by one.</summary>
        Enter,
        /// <summary>The fish spiral up from the touch point, as up a trunk, and fade into the canopy.</summary>
        Climb,
    }

    /// <summary>The procedural voice a kin sings with when it has no Music clip.</summary>
    public enum KinVoice
    {
        Stone,
        Leaves,
        Water,
    }

    /// <summary>A more-than-human kin the wearer can touch, and how the shoal and the music answer it.</summary>
    [Serializable]
    public class KinDefinition
    {
        [Tooltip("Id in code and in the Decisions request. 'other' and 'nothing' are reserved.")]
        public string Id = "rock";

        [Tooltip("What the model reads as this option's meaning.")]
        [TextArea(1, 3)] public string Description = "A rock, stone, or boulder";

        [Tooltip("HUD bar colour and the shoal's tint while matched. Keep it bright: HoloKit is see-through, dark reads as clear.")]
        public Color Color = new(0.55f, 0.85f, 1f);

        public ShoalGesture Gesture = ShoalGesture.Enter;

        [Tooltip("Looped while matched, from the touch point. Empty: the procedural Voice below.")]
        public AudioClip Music;

        public KinVoice Voice = KinVoice.Stone;

        public KinSpec ToSpec() => new(Id, Description);

        public static KinDefinition[] Defaults() => new[]
        {
            new KinDefinition
            {
                Id = "rock",
                Description = "A rock, stone, or boulder",
                Color = new Color(0.55f, 0.85f, 1f),
                Gesture = ShoalGesture.Enter,
                Voice = KinVoice.Stone,
            },
            new KinDefinition
            {
                Id = "tree",
                Description = "A tree: its trunk, bark, branch, or roots (living or fallen)",
                Color = new Color(0.65f, 1f, 0.45f),
                Gesture = ShoalGesture.Climb,
                Voice = KinVoice.Leaves,
            },
        };

        /// <summary>Point mode: things in a room (and a stone brought in) that the fish can be sent to.</summary>
        public static KinDefinition[] PointDefaults() => new[]
        {
            new KinDefinition
            {
                Id = "rock",
                Description = "A rock, stone, or boulder",
                Color = new Color(0.55f, 0.85f, 1f),
                Gesture = ShoalGesture.Enter,
                Voice = KinVoice.Stone,
            },
            new KinDefinition
            {
                Id = "table",
                Description = "A table or desk: its top, edge, or legs",
                Color = new Color(1f, 0.78f, 0.4f),
                Gesture = ShoalGesture.Enter,
                Voice = KinVoice.Leaves,
            },
            new KinDefinition
            {
                Id = "chair",
                Description = "A chair, stool, or seat",
                Color = new Color(1f, 0.55f, 0.9f),
                Gesture = ShoalGesture.Enter,
                Voice = KinVoice.Water,
            },
        };
    }
}

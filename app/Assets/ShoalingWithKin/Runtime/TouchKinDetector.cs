using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
#if UNITY_IOS
using HoloKit.iOS;
#endif

namespace ShoalingWithKin
{
    /// <summary>
    /// Asks Luna Decisions, several times a second, what the wearer's hand is touching (Touch mode) or
    /// pointing at (Point mode), and turns the answers into match / release events with a world point.
    /// Requests are pipelined (up to Max In Flight); late answers for older frames are dropped by the matcher.
    /// </summary>
    public class TouchKinDetector : MonoBehaviour
    {
        const string ModeKey = "ShoalingWithKin.Mode";

        [Tooltip("Touch: skin on a rock or tree. Point: index finger aimed at a rock, table or chair. " +
                 "The Mono HUD button switches it; the device remembers the last choice.")]
        [SerializeField] KinMode m_Mode = KinMode.Point;

        [Tooltip("The kin in Touch mode.")]
        [SerializeField] KinDefinition[] m_Kin = KinDefinition.Defaults();

        [Tooltip("The kin in Point mode.")]
        [SerializeField] KinDefinition[] m_PointKin = KinDefinition.PointDefaults();

        [Tooltip("Point mode: how far along the eye-to-fingertip line (or the gaze) the fish go, in metres. " +
                 "ARKit has no world-ray raycast here, so the distance is fixed.")]
        [SerializeField, Min(0.3f)] float m_PointDistance = 1.5f;

        [Tooltip("OpenRouter model id; must be a System One (Decisions) model that reads images.")]
        [SerializeField] string m_Model = DecisionsProtocol.DefaultModel;

        [Tooltip("Frame width sent to the model. 512 measured best: 256 reads hovering as touching, 768 adds ~100 ms.")]
        [SerializeField, Range(256, 1024)] int m_FrameWidth = 512;

        [SerializeField, Range(30, 95)] int m_JpegQuality = 75;

        [Tooltip("Seconds between frames sent.")]
        [SerializeField, Min(0.05f)] float m_MinInterval = 0.25f;

        [SerializeField, Range(1, 4)] int m_MaxInFlight = 2;

        [SerializeField, Min(1f)] float m_TimeoutSeconds = 4f;

        [Tooltip("A match ends when no answer has arrived for this long (network gone, or paused).")]
        [SerializeField, Min(0.5f)] float m_MatchTimeout = 2f;

        [Tooltip("Save every Nth frame sent, with Luna's answer, to persistentDataPath/luna-frames/<session> (0 = off). " +
                 "Pull with: xcrun devicectl device copy from --domain-type appDataContainer --source Documents/luna-frames")]
        [SerializeField, Min(0)] int m_RecordEvery = 4;

        [SerializeField, Min(1)] int m_RecordLimit = 300;

        [Tooltip("Seconds between summary lines in the device log (0 = off).")]
        [SerializeField, Min(0f)] float m_LogInterval = 2f;

        [SerializeField] KinMatcherSettings m_Matching = KinMatcherSettings.Default;

        [SerializeField] ARCameraManager m_CameraManager;

#if UNITY_IOS
        [Tooltip("Places the touch at the hand (LiDAR depth). Without a fresh hand pose, the touch is at the reach point.")]
        [SerializeField] HandTrackingManager m_HandTracking;
#endif

        /// <summary>A kin was matched at a world point (the hand, or the reach point).</summary>
        public event Action<KinDefinition, Vector3> KinMatched;

        public event Action<KinDefinition> KinReleased;

        /// <summary>A new (non-stale) answer arrived.</summary>
        public event Action Updated;

        /// <summary>The mode (and so <see cref="Kin"/> and <see cref="Outcomes"/>) changed.</summary>
        public event Action ModeChanged;

        LunaDecisionsClient m_Client;
        CameraFrameSampler m_Sampler;
        KinMatcher m_Matcher;
        KinSpec[] m_Specs;
        string m_SessionId;
        long m_Sequence;
        long m_FirstSequence;
        bool m_Capturing;
        float m_CaptureStart;
        float m_NextSend;
        float m_LastAnswerTime;
        float m_NextLog;
        string m_RecordDir;
        int m_Recorded;
        readonly Queue<double> m_AnswerTimes = new();

        public KinMode Mode => m_Mode;
        public string Verb => m_Mode == KinMode.Point ? "Pointing at" : "Touching";
        public IReadOnlyList<KinDefinition> Kin => ActiveKin;
        KinDefinition[] ActiveKin => m_Mode == KinMode.Point ? m_PointKin : m_Kin;
        public IReadOnlyList<string> Outcomes { get; private set; } = Array.Empty<string>();
        public KinMatcher Matcher => m_Matcher;
        public Texture2D LastFrame => m_Sampler?.LastFrame;
        public string Model => m_Model;
        public bool HasKey => m_Client != null;
        public bool Running { get; set; } = true;
        public int InFlight { get; private set; }
        public int Requests { get; private set; }
        public int Answers { get; private set; }
        public int Errors { get; private set; }
        public string LastError { get; private set; }
        public float LastLatency { get; private set; }
        public float MeanLatency { get; private set; }
        public double TotalCost { get; private set; }
        /// <summary>Where the last match happened.</summary>
        public Vector3 TouchPoint { get; private set; }

        /// <summary>Where the latest answer's frame puts the fish: the hand or reach point (Touch), what is pointed at (Point).</summary>
        public Vector3 ObservedPoint { get; private set; }

        public bool HandVisible { get; private set; }

        /// <summary>Answers per second over the last three seconds.</summary>
        public float AnswerRate
        {
            get
            {
                double now = Time.realtimeSinceStartupAsDouble;
                while (m_AnswerTimes.Count > 0 && now - m_AnswerTimes.Peek() > 3.0)
                    m_AnswerTimes.Dequeue();
                return m_AnswerTimes.Count / 3f;
            }
        }

        public KinDefinition Find(string id) => Array.Find(ActiveKin, k => k.Id == id);

        // Before any Start, so subscribers that read Kin in their Start see the remembered mode.
        void Awake() => m_Mode = (KinMode)PlayerPrefs.GetInt(ModeKey, (int)m_Mode);

        void Start()
        {
            if (m_CameraManager == null)
                m_CameraManager = FindFirstObjectByType<ARCameraManager>();
#if UNITY_IOS
            if (m_HandTracking == null)
                m_HandTracking = FindFirstObjectByType<HandTrackingManager>();
#endif
            Configure();
            m_Sampler = new CameraFrameSampler(m_CameraManager, m_FrameWidth, m_JpegQuality);
            m_SessionId = "shoaling-with-kin-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            var key = OpenRouterKey.Load();
            if (string.IsNullOrEmpty(key))
            {
                LastError = $"No OpenRouter key: set {OpenRouterKey.EnvironmentVariable} and rerun the setup.";
                Debug.LogError("[TouchKinDetector] " + LastError);
                return;
            }
            m_Client = new LunaDecisionsClient(key, m_TimeoutSeconds);
            Debug.Log($"[TouchKinDetector] {m_Mode} mode: asking {m_Model} about {string.Join(", ", Outcomes)}");
        }

        /// <summary>Switches between touching and pointing: releases any match and ignores answers to the old question.</summary>
        public void SetMode(KinMode mode)
        {
            if (mode == m_Mode)
                return;
            if (m_Matcher?.Matched != null)
                Apply(m_Matcher.ForceRelease(), latency: 0f);
            m_Mode = mode;
            PlayerPrefs.SetInt(ModeKey, (int)mode);
            Configure();
            Debug.Log($"[TouchKinDetector] {m_Mode} mode: asking about {string.Join(", ", Outcomes)}");
            ModeChanged?.Invoke();
        }

        void Configure()
        {
            m_Specs = Array.ConvertAll(ActiveKin, k => k.ToSpec());
            Outcomes = DecisionsProtocol.Outcomes(m_Specs);
            m_Matcher = new KinMatcher(Array.ConvertAll(ActiveKin, k => k.Id), m_Matching);
            m_FirstSequence = m_Sequence + 1;
        }

        void OnDestroy() => m_Sampler?.Dispose();

        // Stopping the GameObject stops its coroutines mid-request; start clean when it comes back.
        void OnDisable()
        {
            StopAllCoroutines();
            m_Capturing = false;
            InFlight = 0;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (m_LogInterval > 0f && now >= m_NextLog && m_Matcher != null)
            {
                m_NextLog = now + m_LogInterval;
                Debug.Log($"[Luna] {m_Mode}: {Answers}/{Requests} answered, {Errors} errors, {AnswerRate:F1} Hz, " +
                          $"last {LastLatency * 1000f:F0} ms, avg {MeanLatency * 1000f:F0} ms, " +
                          $"top {m_Matcher.Leading ?? "-"} {m_Matcher.LeadingProbability:F2}, matched {m_Matcher.Matched ?? "-"}, " +
                          $"hand {(HandVisible ? "yes" : "no")}, ${TotalCost:F4}");
            }
            if (m_Matcher?.Matched != null && (!Running || now - m_LastAnswerTime > m_MatchTimeout))
                Apply(m_Matcher.ForceRelease(), latency: 0f);
            // A capture should take a frame or two; never let a lost one block sampling.
            if (m_Capturing && now - m_CaptureStart > 2f)
                m_Capturing = false;
            if (!Running || m_Client == null || m_Capturing || InFlight >= m_MaxInFlight || now < m_NextSend)
                return;
            m_NextSend = now + m_MinInterval;
            StartCoroutine(SampleAndAsk());
        }

        IEnumerator SampleAndAsk()
        {
            long sequence = ++m_Sequence;
            var point = TargetPoint(out bool hand);
            byte[] jpeg = null;
            m_Capturing = true;
            m_CaptureStart = Time.unscaledTime;
            yield return m_Sampler.Capture(bytes => jpeg = bytes);
            m_Capturing = false;
            if (jpeg == null)
                yield break;

            var question = m_Mode == KinMode.Point ? DecisionsQuestion.Point : DecisionsQuestion.Touch;
            var json = DecisionsProtocol.BuildRequestJson(m_Model, Convert.ToBase64String(jpeg), m_Specs, m_SessionId, question);
            InFlight++;
            Requests++;
            yield return m_Client.Send(json,
                (decision, latency) =>
                {
                    Record(jpeg, decision, (float)latency, sequence, hand);
                    OnDecision(decision, (float)latency, sequence, hand, point);
                },
                error =>
                {
                    Errors++;
                    LastError = error;
                    Debug.LogWarning("[TouchKinDetector] " + error);
                });
            InFlight--;
        }

        void OnDecision(TouchDecision decision, float latency, long sequence, bool hand, Vector3 point)
        {
            Answers++;
            LastLatency = latency;
            MeanLatency = Answers == 1 ? latency : Mathf.Lerp(MeanLatency, latency, 0.1f);
            TotalCost += decision.Cost;
            m_AnswerTimes.Enqueue(Time.realtimeSinceStartupAsDouble);
            if (sequence < m_FirstSequence)
                return; // asked before a mode switch

            var change = m_Matcher.Observe(decision.Probabilities, sequence);
            if (change.Stale)
                return;
            m_LastAnswerTime = Time.unscaledTime;
            HandVisible = hand;
            ObservedPoint = point;
            Apply(change, latency);
        }

        /// <summary>Keeps every Nth frame and its answer on the device, to check orientation and accuracy afterwards.</summary>
        void Record(byte[] jpeg, TouchDecision decision, float latency, long sequence, bool hand)
        {
            if (m_RecordEvery <= 0 || m_Recorded >= m_RecordLimit || sequence % m_RecordEvery != 0)
                return;
            try
            {
                m_RecordDir ??= Path.Combine(Application.persistentDataPath, "luna-frames", m_SessionId);
                Directory.CreateDirectory(m_RecordDir);
                var name = sequence.ToString("D6", CultureInfo.InvariantCulture);
                File.WriteAllBytes(Path.Combine(m_RecordDir, name + ".jpg"), jpeg);
                var line = new StringBuilder();
                line.Append(FormattableString.Invariant($"{{\"frame\":\"{name}.jpg\",\"ms\":{latency * 1000f:F0},\"hand\":{(hand ? "true" : "false")},\"choice\":\"{decision.Choice}\",\"p\":{{"));
                bool first = true;
                foreach (var (id, p) in decision.Probabilities)
                {
                    line.Append(FormattableString.Invariant($"{(first ? "" : ",")}\"{id}\":{p:F3}"));
                    first = false;
                }
                line.Append("}}\n");
                File.AppendAllText(Path.Combine(m_RecordDir, "answers.jsonl"), line.ToString());
                m_Recorded++;
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[TouchKinDetector] Frame recording off: {e.Message}");
                m_RecordEvery = 0;
            }
        }

        /// <summary>Raises release, match and update events; a throwing subscriber cannot stall detection.</summary>
        void Apply(MatchChange change, float latency)
        {
            try
            {
                if (change.Released != null)
                {
                    Debug.Log($"[TouchKinDetector] Released {change.Released}");
                    KinReleased?.Invoke(Find(change.Released));
                }
                if (change.Matched != null)
                {
                    TouchPoint = ObservedPoint;
                    Debug.Log($"[TouchKinDetector] Matched {change.Matched} p={m_Matcher.LeadingProbability:F2} " +
                              $"at {TouchPoint:F2} ({(HandVisible ? "hand" : "reach point")}), {latency * 1000f:F0} ms");
                    KinMatched?.Invoke(Find(change.Matched), TouchPoint);
                }
                Updated?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Where the fish go for this frame. Touch: the palm, or the reach point when no hand is seen. Point: a point
        /// on the line from the eye through the index fingertip (as people aim), or along the gaze.
        /// </summary>
        Vector3 TargetPoint(out bool hand)
        {
            hand = TryGetHandJoints(out var palm, out var indexTip);
            if (m_Mode == KinMode.Touch)
                return hand ? palm : HeadPose.ReachPoint();
            var head = HeadPose.Get();
            if (head == null)
                return HeadPose.ReachPoint();
            var direction = hand ? indexTip - head.position : Quaternion.AngleAxis(10f, head.right) * head.forward;
            return head.position + direction.normalized * m_PointDistance;
        }

        /// <summary>The palm's middle (middle finger MCP to tip) and the index fingertip, when a hand was seen in the last half second.</summary>
        bool TryGetHandJoints(out Vector3 palm, out Vector3 indexTip)
        {
            palm = indexTip = default;
#if UNITY_IOS
            if (m_HandTracking == null || m_HandTracking.HandPoseDetector == null || m_HandTracking.HandCount == 0)
                return false;
            if (Time.realtimeSinceStartupAsDouble - m_HandTracking.HandPoseDetector.LastUpdateTime > 0.5)
                return false;
            var joints = m_HandTracking.HandPoses3D[0];
            if (!joints.TryGetValue(JointName.MiddleMCP, out var mcp) || !joints.TryGetValue(JointName.MiddleTip, out var tip)
                || !joints.TryGetValue(JointName.IndexTip, out indexTip))
                return false;
            palm = (mcp + tip) * 0.5f;
            return true;
#else
            return false;
#endif
        }
    }
}

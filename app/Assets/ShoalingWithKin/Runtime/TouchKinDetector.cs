using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
#if UNITY_IOS
using HoloKit.iOS;
#endif

namespace ShoalingWithKin
{
    /// <summary>
    /// Asks Luna Decisions, several times a second, what the wearer's hand is touching, and turns
    /// the answers into match / release events with the touch point. Requests are pipelined (up to
    /// Max In Flight); late answers for older frames are dropped by the matcher.
    /// </summary>
    public class TouchKinDetector : MonoBehaviour
    {
        [SerializeField] KinDefinition[] m_Kin = KinDefinition.Defaults();

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

        LunaDecisionsClient m_Client;
        CameraFrameSampler m_Sampler;
        KinMatcher m_Matcher;
        KinSpec[] m_Specs;
        string m_SessionId;
        long m_Sequence;
        bool m_Capturing;
        float m_CaptureStart;
        float m_NextSend;
        float m_LastAnswerTime;
        readonly Queue<double> m_AnswerTimes = new();

        public IReadOnlyList<KinDefinition> Kin => m_Kin;
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

        /// <summary>The hand (or reach point) in the frame behind the latest answer.</summary>
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

        public KinDefinition Find(string id) => Array.Find(m_Kin, k => k.Id == id);

        void Start()
        {
            if (m_CameraManager == null)
                m_CameraManager = FindFirstObjectByType<ARCameraManager>();
#if UNITY_IOS
            if (m_HandTracking == null)
                m_HandTracking = FindFirstObjectByType<HandTrackingManager>();
#endif
            m_Specs = Array.ConvertAll(m_Kin, k => k.ToSpec());
            Outcomes = DecisionsProtocol.Outcomes(m_Specs);
            m_Matcher = new KinMatcher(Array.ConvertAll(m_Kin, k => k.Id), m_Matching);
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
            Debug.Log($"[TouchKinDetector] Asking {m_Model} about: {string.Join(", ", Outcomes)}");
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
            bool hand = TryGetHandPoint(out var handPoint);
            byte[] jpeg = null;
            m_Capturing = true;
            m_CaptureStart = Time.unscaledTime;
            yield return m_Sampler.Capture(bytes => jpeg = bytes);
            m_Capturing = false;
            if (jpeg == null)
                yield break;

            var json = DecisionsProtocol.BuildRequestJson(m_Model, Convert.ToBase64String(jpeg), m_Specs, m_SessionId);
            InFlight++;
            Requests++;
            yield return m_Client.Send(json,
                (decision, latency) => OnDecision(decision, (float)latency, sequence, hand, handPoint),
                error =>
                {
                    Errors++;
                    LastError = error;
                    Debug.LogWarning("[TouchKinDetector] " + error);
                });
            InFlight--;
        }

        void OnDecision(TouchDecision decision, float latency, long sequence, bool hand, Vector3 handPoint)
        {
            Answers++;
            LastLatency = latency;
            MeanLatency = Answers == 1 ? latency : Mathf.Lerp(MeanLatency, latency, 0.1f);
            TotalCost += decision.Cost;
            m_AnswerTimes.Enqueue(Time.realtimeSinceStartupAsDouble);

            var change = m_Matcher.Observe(decision.Probabilities, sequence);
            if (change.Stale)
                return;
            m_LastAnswerTime = Time.unscaledTime;
            HandVisible = hand;
            ObservedPoint = hand ? handPoint : HeadPose.ReachPoint();
            Apply(change, latency);
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

        /// <summary>The palm's middle (middle finger MCP to tip) when a hand was seen in the last half second.</summary>
        bool TryGetHandPoint(out Vector3 point)
        {
            point = default;
#if UNITY_IOS
            if (m_HandTracking == null || m_HandTracking.HandPoseDetector == null || m_HandTracking.HandCount == 0)
                return false;
            if (Time.realtimeSinceStartupAsDouble - m_HandTracking.HandPoseDetector.LastUpdateTime > 0.5)
                return false;
            var joints = m_HandTracking.HandPoses3D[0];
            if (!joints.TryGetValue(JointName.MiddleMCP, out var mcp) || !joints.TryGetValue(JointName.MiddleTip, out var tip))
                return false;
            point = (mcp + tip) * 0.5f;
            return true;
#else
            return false;
#endif
        }
    }
}

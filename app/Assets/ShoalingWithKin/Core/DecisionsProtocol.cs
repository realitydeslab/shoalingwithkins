using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShoalingWithKin
{
    /// <summary>A kin the wearer can touch: an id for code and a description the model reads.</summary>
    public readonly struct KinSpec
    {
        public readonly string Id;
        public readonly string Description;

        public KinSpec(string id, string description)
        {
            Id = id;
            Description = description;
        }
    }

    /// <summary>One answer from the Decisions API: the touch distribution over kin, other and nothing.</summary>
    public sealed class TouchDecision
    {
        public string Choice;
        public float Confidence;
        public Dictionary<string, float> Probabilities = new();
        public int InputTokens;
        public double Cost;
        public string Model;
    }

    /// <summary>
    /// The System One request and response for "what is the wearer's hand touching?".
    /// Endpoint: POST https://openrouter.ai/api/v1/systemone. The image goes in <c>state</c> as an
    /// OpenAI chat-style <c>image_url</c> part: it is the only shape the endpoint reads as an image
    /// (other shapes are read as base64 text, ~21k tokens and wrong answers).
    /// </summary>
    public static class DecisionsProtocol
    {
        public const string Endpoint = "https://openrouter.ai/api/v1/systemone";
        public const string DefaultModel = "openai/gpt-6-luna-decisions";
        public const string QuestionId = "touch";
        public const string OtherId = "other";
        public const string NothingId = "nothing";

        public const string Context =
            "First-person view from a camera worn on the person's face while they walk along a creek. " +
            "Any hand or arm reaching in from the bottom of the frame belongs to the wearer.";

        public const string Instructions =
            "What is the wearer's hand physically touching right now? Skin must be in contact; hovering near, " +
            "pointing at, or casting a shadow on something is not touching.";

        const string OtherCriterion = "Something else: water, ground, railing, clothing, another object";
        const string NothingCriterion = "No hand visible, or the hand touches nothing";

        /// <summary>Kin ids plus the two outcomes every request carries, in display order.</summary>
        public static List<string> Outcomes(IReadOnlyList<KinSpec> kin)
        {
            var ids = new List<string>(kin.Count + 2);
            foreach (var k in kin)
                ids.Add(k.Id);
            ids.Add(OtherId);
            ids.Add(NothingId);
            return ids;
        }

        public static string BuildRequestJson(string model, string jpegBase64, IReadOnlyList<KinSpec> kin, string sessionId = null)
        {
            if (kin == null || kin.Count == 0)
                throw new ArgumentException("At least one kin is needed.", nameof(kin));
            var criteria = new JObject();
            foreach (var k in kin)
            {
                if (k.Id == OtherId || k.Id == NothingId)
                    throw new ArgumentException($"'{k.Id}' is reserved.", nameof(kin));
                criteria[k.Id] = k.Description;
            }
            criteria[OtherId] = OtherCriterion;
            criteria[NothingId] = NothingCriterion;

            var body = new JObject
            {
                ["model"] = model,
                ["state"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = Context },
                    new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject { ["url"] = "data:image/jpeg;base64," + jpegBase64 },
                    },
                },
                ["questions"] = new JObject
                {
                    [QuestionId] = new JObject
                    {
                        ["type"] = "choice",
                        ["instructions"] = Instructions,
                        ["criteria"] = criteria,
                    },
                },
            };
            if (!string.IsNullOrEmpty(sessionId))
                body["session_id"] = sessionId;
            return body.ToString(Formatting.None);
        }

        /// <summary>Parses a 200 response. Throws <see cref="FormatException"/> when the touch answer is missing.</summary>
        public static TouchDecision ParseResponse(string json)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonReaderException e)
            {
                throw new FormatException("Response is not JSON.", e);
            }
            if ((root["answers"] as JObject)?[QuestionId] is not JObject answer || answer["probabilities"] is not JObject probs)
                throw new FormatException($"Response has no '{QuestionId}' choice answer: {Truncate(json)}");

            try
            {
                var usage = root["usage"] as JObject;
                var decision = new TouchDecision
                {
                    Choice = (string)answer["choice"],
                    Confidence = (float?)answer["confidence"] ?? 0f,
                    InputTokens = (int?)usage?["input_tokens"] ?? 0,
                    Cost = (double?)usage?["cost"] ?? 0d,
                    Model = (string)root["model"],
                };
                foreach (var p in probs.Properties())
                    decision.Probabilities[p.Name] = (float?)p.Value ?? 0f;
                return decision;
            }
            catch (ArgumentException e)
            {
                throw new FormatException($"Response has a value of the wrong type: {Truncate(json)}", e);
            }
        }

        /// <summary>The <c>error.message</c> of an error body, or the body itself when it has none.</summary>
        public static string ParseError(string json)
        {
            try
            {
                var error = JObject.Parse(json)["error"];
                var message = error is JObject e ? (string)e["message"] : error?.Type == JTokenType.String ? (string)error : null;
                if (!string.IsNullOrEmpty(message))
                    return Truncate(message);
            }
            catch (JsonException)
            {
            }
            catch (ArgumentException)
            {
            }
            return Truncate(json);
        }

        static string Truncate(string s) => s == null ? "" : s.Length <= 300 ? s : s.Substring(0, 300) + "…";
    }
}

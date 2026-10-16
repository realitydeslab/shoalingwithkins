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

    /// <summary>
    /// How the wearer picks a kin, and the words Luna reads for it. Keep in sync with tools/bench_touch.py.
    /// </summary>
    public sealed class DecisionsQuestion
    {
        public readonly string Context;
        public readonly string Instructions;
        public readonly string OtherCriterion;
        public readonly string NothingCriterion;

        DecisionsQuestion(string context, string instructions, string other, string nothing)
        {
            Context = context;
            Instructions = instructions;
            OtherCriterion = other;
            NothingCriterion = nothing;
        }

        /// <summary>Skin contact only: rock and tree at the creek (44/44 on tools/testset).</summary>
        public static readonly DecisionsQuestion Touch = new(
            "First-person view from a camera worn on the person's face while they walk along a creek. " +
            "Any hand or arm reaching in from the bottom of the frame belongs to the wearer.",
            "What is the wearer's hand physically touching right now? Skin must be in contact; hovering near, " +
            "pointing at, or casting a shadow on something is not touching.",
            "Something else: water, ground, railing, clothing, another object",
            "No hand visible, or the hand touches nothing");

        /// <summary>What the index finger is aimed at, or the hand touches (30/30 on tools/testset-point).</summary>
        public static readonly DecisionsQuestion Point = new(
            "First-person view from a camera worn on the person's face. " +
            "Any hand or arm reaching in from the bottom of the frame belongs to the wearer.",
            "What is the wearer's hand pointing at, or touching, right now? Follow the extended index finger " +
            "to the first object it is aimed at. A hand that is open, waving, or relaxed and touches nothing " +
            "is not pointing.",
            "Something else: wall, window, floor, screen, water, another object",
            "No hand visible, or the hand neither points at nor touches anything");
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
    /// The System One request and response for "what is the wearer's hand touching (or pointing at)?".
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

        /// <summary>The request body; <paramref name="question"/> defaults to <see cref="DecisionsQuestion.Touch"/>.</summary>
        public static string BuildRequestJson(string model, string jpegBase64, IReadOnlyList<KinSpec> kin, string sessionId = null,
            DecisionsQuestion question = null)
        {
            question ??= DecisionsQuestion.Touch;
            if (kin == null || kin.Count == 0)
                throw new ArgumentException("At least one kin is needed.", nameof(kin));
            var criteria = new JObject();
            foreach (var k in kin)
            {
                if (k.Id == OtherId || k.Id == NothingId)
                    throw new ArgumentException($"'{k.Id}' is reserved.", nameof(kin));
                criteria[k.Id] = k.Description;
            }
            criteria[OtherId] = question.OtherCriterion;
            criteria[NothingId] = question.NothingCriterion;

            var body = new JObject
            {
                ["model"] = model,
                ["state"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = question.Context },
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
                        ["instructions"] = question.Instructions,
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

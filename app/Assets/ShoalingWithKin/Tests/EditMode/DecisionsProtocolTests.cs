using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ShoalingWithKin.Tests
{
    public class DecisionsProtocolTests
    {
        static readonly KinSpec[] Kin =
        {
            new("rock", "A rock, stone, or boulder"),
            new("tree", "A tree: its trunk, bark, branch, or roots (living or fallen)"),
        };

        // Captured from openai/gpt-6-luna-decisions on 2026-10-08 (touch_rock_1, 512 px).
        const string RealResponse =
            "{\"model\":\"openai/gpt-6-luna-decisions-20261006\",\"answers\":{\"touch\":{\"type\":\"choice\",\"choice\":\"rock\"," +
            "\"probabilities\":{\"rock\":1,\"tree\":0,\"other\":0,\"nothing\":0},\"confidence\":1}}," +
            "\"usage\":{\"input_tokens\":428,\"output_tokens\":0,\"cost\":0.0000428},\"id\":\"gen-dec-1791464590-ckUSsBoo6ZW30g3aLzWo\",\"provider\":\"OpenAI\"}";

        [Test]
        public void RequestPutsTheImageInStateAsAnImageUrlPart()
        {
            var body = JObject.Parse(DecisionsProtocol.BuildRequestJson(DecisionsProtocol.DefaultModel, "QUJD", Kin, "s1"));
            Assert.AreEqual("openai/gpt-6-luna-decisions", (string)body["model"]);
            Assert.AreEqual("s1", (string)body["session_id"]);
            var state = (JArray)body["state"];
            Assert.AreEqual("text", (string)state[0]["type"]);
            Assert.AreEqual("image_url", (string)state[1]["type"]);
            Assert.AreEqual("data:image/jpeg;base64,QUJD", (string)state[1]["image_url"]["url"]);
        }

        [Test]
        public void RequestAsksOneChoiceOverKinOtherAndNothing()
        {
            var body = JObject.Parse(DecisionsProtocol.BuildRequestJson("m", "QUJD", Kin));
            var question = (JObject)body["questions"][DecisionsProtocol.QuestionId];
            Assert.AreEqual("choice", (string)question["type"]);
            var criteria = (JObject)question["criteria"];
            CollectionAssert.AreEqual(new[] { "rock", "tree", "other", "nothing" }, criteria.Properties().Select(p => p.Name));
            Assert.IsNull(body["session_id"]);
        }

        [Test]
        public void ReservedIdsAreRejected()
        {
            Assert.Throws<ArgumentException>(() =>
                DecisionsProtocol.BuildRequestJson("m", "x", new[] { new KinSpec("nothing", "n") }));
        }

        [Test]
        public void ParsesARealResponse()
        {
            var d = DecisionsProtocol.ParseResponse(RealResponse);
            Assert.AreEqual("rock", d.Choice);
            Assert.AreEqual(1f, d.Probabilities["rock"]);
            Assert.AreEqual(0f, d.Probabilities["nothing"]);
            Assert.AreEqual(4, d.Probabilities.Count);
            Assert.AreEqual(428, d.InputTokens);
            Assert.AreEqual(0.0000428, d.Cost, 1e-9);
        }

        [Test]
        public void ResponseWithoutTheAnswerIsAFormatError()
        {
            Assert.Throws<FormatException>(() => DecisionsProtocol.ParseResponse("{\"answers\":{}}"));
            Assert.Throws<FormatException>(() => DecisionsProtocol.ParseResponse("<html>"));
        }

        [Test]
        public void ErrorBodyGivesItsMessage()
        {
            Assert.AreEqual("Invalid input", DecisionsProtocol.ParseError("{\"error\":{\"message\":\"Invalid input\",\"code\":400}}"));
            Assert.AreEqual("Bad Gateway", DecisionsProtocol.ParseError("Bad Gateway"));
        }

        [Test]
        public void OutcomesAreKinThenOtherThenNothing()
        {
            CollectionAssert.AreEqual(new[] { "rock", "tree", "other", "nothing" }, DecisionsProtocol.Outcomes(Kin));
        }
    
        [Test]
        public void NullUsageAndNullProbabilityParseWithoutThrowing()
        {
            var d = DecisionsProtocol.ParseResponse(
                "{\"answers\":{\"touch\":{\"choice\":\"tree\",\"probabilities\":{\"rock\":null,\"tree\":0.9}}},\"usage\":null}");
            Assert.AreEqual(0f, d.Probabilities["rock"]);
            Assert.AreEqual(0.9f, d.Probabilities["tree"], 1e-6f);
            Assert.AreEqual(0, d.InputTokens);
        }

        [Test]
        public void WrongShapesAreFormatErrors()
        {
            Assert.Throws<FormatException>(() => DecisionsProtocol.ParseResponse("{\"answers\":\"oops\"}"));
            Assert.Throws<FormatException>(() =>
                DecisionsProtocol.ParseResponse("{\"answers\":{\"touch\":{\"probabilities\":{\"rock\":[1]}}}}"));
        }

        [Test]
        public void ErrorAsAPlainStringIsReturned()
        {
            Assert.AreEqual("rate limited", DecisionsProtocol.ParseError("{\"error\":\"rate limited\"}"));
            Assert.AreEqual("[1]", DecisionsProtocol.ParseError("[1]"));
        }
    }
}

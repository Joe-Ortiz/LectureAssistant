using System.Text.RegularExpressions;
using LectureAssistant.Core.Models;
using LectureAssistant.QuestionGeneration.Local;
using LLama.Sampling;

namespace LectureAssistant.QuestionGeneration.Tests;

/// <summary>
/// llama.cpp only parses GBNF when a sampler is built against a loaded model's vocabulary, so these tests
/// check structure (every referenced rule defined, literals well-formed) rather than running the parser.
/// </summary>
public partial class QuestionGrammarTests
{
    public static TheoryData<QuestionType[], int> Cases => new()
    {
        { [QuestionType.MultipleChoice, QuestionType.TrueFalse, QuestionType.FillInTheBlank], 5 },
        { [QuestionType.MultipleChoice], 1 },
        { [QuestionType.TrueFalse], 3 },
        { [QuestionType.FillInTheBlank, QuestionType.TrueFalse], 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_referenced_rule_is_defined_exactly_once(QuestionType[] types, int count)
    {
        var gbnf = QuestionGrammar.Build(types, count);
        var rules = ParseRules(gbnf);

        Assert.Contains(QuestionGrammar.Root, rules.Keys);
        foreach (var (name, body) in rules)
        {
            foreach (Match m in Identifier().Matches(StripLiterals(body)))
                Assert.True(rules.ContainsKey(m.Value), $"Rule '{name}' references undefined rule '{m.Value}'.");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Literals_and_brackets_are_balanced(QuestionType[] types, int count)
    {
        foreach (var (name, body) in ParseRules(QuestionGrammar.Build(types, count)))
        {
            var rest = StripLiterals(body);
            Assert.DoesNotContain("\"", rest);
            Assert.Equal(rest.Count(c => c == '('), rest.Count(c => c == ')'));
            Assert.Equal(rest.Count(c => c == '{'), rest.Count(c => c == '}'));
        }
    }

    [Fact]
    public void Only_allowed_type_branches_are_included()
    {
        var gbnf = QuestionGrammar.Build([QuestionType.TrueFalse], 2);
        Assert.Contains("\\\"true_false\\\"", gbnf);
        Assert.DoesNotContain("multiple_choice", gbnf);
        Assert.DoesNotContain("fill_in_the_blank", gbnf);
    }

    [Theory]
    [InlineData(1, "question ws \"]\"")]
    [InlineData(4, "(ws \",\" ws question){3}")]
    public void Question_count_is_exact(int count, string expected)
    {
        Assert.Contains(expected, QuestionGrammar.Build([QuestionType.TrueFalse], count));
    }

    [Theory]
    [InlineData("mc-body")]
    [InlineData("tf-body")]
    [InlineData("fib-body")]
    public void Explanation_comes_before_the_answer_fields(string branch)
    {
        var body = ParseRules(QuestionGrammar.Build(Enum.GetValues<QuestionType>(), 3))[branch];
        string[] order = ["key-prompt", "key-explanation", "key-options", "key-correct", "key-accepted"];
        var positions = order.Select(key => body.IndexOf(key, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.All(order, key => Assert.Equal(positions[Array.IndexOf(order, key)], body.LastIndexOf(key, StringComparison.Ordinal)));
    }

    [Fact]
    public void Grammar_object_can_be_constructed_without_a_model()
    {
        var grammar = new Grammar(QuestionGrammar.Build(Enum.GetValues<QuestionType>(), 3), QuestionGrammar.Root);
        Assert.Equal("root", grammar.Root);
    }

    private static Dictionary<string, string> ParseRules(string gbnf)
    {
        var rules = new Dictionary<string, string>();
        foreach (var line in gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = RuleLine().Match(line);
            Assert.True(match.Success, $"Not a rule: {line}");
            Assert.True(rules.TryAdd(match.Groups[1].Value, match.Groups[2].Value), $"Duplicate rule {match.Groups[1].Value}");
        }
        return rules;
    }

    /// <summary>Removes "..." literals (with escapes) and [...] character classes.</summary>
    private static string StripLiterals(string body) => CharClass().Replace(StringLiteral().Replace(body, " "), " ");

    [GeneratedRegex(@"^([a-z][a-z0-9-]*) ::= (.*)$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\[(?:[^\]\\]|\\.)*\]")]
    private static partial Regex CharClass();

    [GeneratedRegex(@"[a-z][a-z0-9-]*")]
    private static partial Regex Identifier();
}

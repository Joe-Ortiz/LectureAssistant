using System.Text;
using LectureAssistant.Core.Models;

namespace LectureAssistant.QuestionGeneration.Local;

/// <summary>
/// GBNF grammar for <see cref="QuestionDraftSet"/> JSON, used to constrain local-model sampling so even a
/// small model can only emit well-formed output. Stricter than the JSON schema: each question type has its
/// own branch, so multiple choice always has 3-5 options, fill-in-the-blank prompts contain exactly one
/// "___" and at least one accepted answer, and the array has exactly the requested number of questions.
/// Field order matches <see cref="QuestionDraftSchema.BuildJsonSchema"/> (the excerpt first, to ground the question).
/// </summary>
internal static class QuestionGrammar
{
    public const string Root = "root";

    public static string Build(IEnumerable<QuestionType> allowedTypes, int questionCount)
    {
        if (questionCount < 1) throw new ArgumentOutOfRangeException(nameof(questionCount));
        var types = allowedTypes.Distinct().Order().ToList();
        if (types.Count == 0) types = [.. Enum.GetValues<QuestionType>()];

        var sb = new StringBuilder();
        sb.Append("root ::= \"{\" ws \"\\\"questions\\\"\" ws \":\" ws \"[\" ws question");
        if (questionCount > 1) sb.Append(" (ws \",\" ws question){").Append(questionCount - 1).Append('}');
        sb.Append(" ws \"]\" ws \"}\"\n");

        sb.Append("""
            question ::= "{" ws key-excerpt string-nonempty ws "," ws key-time number ws "," ws key-type question-body ws "}"
            """).Append('\n');
        sb.Append("question-body ::= ").AppendJoin(" | ", types.Select(t => t switch
        {
            QuestionType.MultipleChoice => "mc-body",
            QuestionType.TrueFalse => "tf-body",
            _ => "fib-body",
        })).Append('\n');

        if (types.Contains(QuestionType.MultipleChoice))
        {
            sb.Append("""
                mc-body ::= "\"multiple_choice\"" ws "," ws key-prompt string-nonempty ws "," ws key-options "[" ws option (ws "," ws option){2,4} ws "]" ws "," ws key-correct "false" ws "," ws key-accepted "[" ws "]" ws "," ws key-explanation string-nonempty
                option ::= "{" ws "\"text\"" ws ":" ws string-nonempty ws "," ws "\"is_correct\"" ws ":" ws boolean ws "," ws "\"feedback\"" ws ":" ws string ws "}"
                """).Append('\n');
        }
        if (types.Contains(QuestionType.TrueFalse))
        {
            sb.Append("""
                tf-body ::= "\"true_false\"" ws "," ws key-prompt string-nonempty ws "," ws key-options "[" ws "]" ws "," ws key-correct boolean ws "," ws key-accepted "[" ws "]" ws "," ws key-explanation string-nonempty
                """).Append('\n');
        }
        if (types.Contains(QuestionType.FillInTheBlank))
        {
            sb.Append("""
                fib-body ::= "\"fill_in_the_blank\"" ws "," ws key-prompt blank-string ws "," ws key-options "[" ws "]" ws "," ws key-correct "false" ws "," ws key-accepted "[" ws string-nonempty (ws "," ws string-nonempty){0,7} ws "]" ws "," ws key-explanation string-nonempty
                blank-string ::= "\"" no-underscore-char* "___" no-underscore-char* "\""
                no-underscore-char ::= [^"\\_\x7F\x00-\x1F] | escape
                """).Append('\n');
        }

        sb.Append("""
            key-excerpt ::= "\"source_excerpt\"" ws ":" ws
            key-time ::= "\"timestamp_seconds\"" ws ":" ws
            key-type ::= "\"type\"" ws ":" ws
            key-prompt ::= "\"prompt\"" ws ":" ws
            key-options ::= "\"options\"" ws ":" ws
            key-correct ::= "\"correct_answer\"" ws ":" ws
            key-accepted ::= "\"accepted_answers\"" ws ":" ws
            key-explanation ::= "\"explanation\"" ws ":" ws
            boolean ::= "true" | "false"
            number ::= [0-9]{1,6} ("." [0-9]{1,3})?
            string ::= "\"" char* "\""
            string-nonempty ::= "\"" char+ "\""
            char ::= [^"\\\x7F\x00-\x1F] | escape
            escape ::= "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})
            ws ::= | " " | "\n" [ \t]{0,20}
            """).Append('\n');

        return sb.ToString();
    }
}

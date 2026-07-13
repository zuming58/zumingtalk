using System.Text;

namespace Zumingtalk.Infrastructure.Asr;

internal sealed class AliyunTranscriptAggregator
{
    private readonly List<string> finalizedSentences = [];
    private string interimText = string.Empty;

    public void ApplyResult(string eventName, string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        if (string.Equals(eventName, "SentenceEnd", StringComparison.OrdinalIgnoreCase))
        {
            AddFinalSentence(result);
            interimText = string.Empty;
            return;
        }

        if (string.Equals(eventName, "TranscriptionResultChanged", StringComparison.OrdinalIgnoreCase))
        {
            interimText = result;
        }
    }

    public string GetText()
    {
        var builder = new StringBuilder();
        foreach (var sentence in finalizedSentences)
        {
            builder.Append(sentence);
        }

        if (!string.IsNullOrWhiteSpace(interimText) &&
            (finalizedSentences.Count == 0 || !string.Equals(finalizedSentences[^1], interimText, StringComparison.Ordinal)))
        {
            builder.Append(interimText);
        }

        return builder.ToString();
    }

    private void AddFinalSentence(string sentence)
    {
        if (finalizedSentences.Count > 0 && string.Equals(finalizedSentences[^1], sentence, StringComparison.Ordinal))
        {
            return;
        }

        finalizedSentences.Add(sentence);
    }
}

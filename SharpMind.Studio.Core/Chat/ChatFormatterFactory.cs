using SharpMind.Inference.Chat.PromptFormatters;
using SharpMind.Model.Format;

namespace SharpMind.Studio.Core.Chat;

public static class ChatFormatterFactory
{
    public static IChatPromptFormatter? Create(string? strategy, ModelMetaData? meta)
    {
        string? template = meta?.GetChatTemplate();
        return strategy?.Trim().ToLowerInvariant() switch
        {
            "jinja" => string.IsNullOrWhiteSpace(template)
                ? new SimpleFormatter()
                : new JinjaTemplateFormatter(template),
            "chatml" => string.IsNullOrWhiteSpace(template)
                ? new SimpleFormatter()
                : new ChatMLFormatter(template),
            "simple" => new SimpleFormatter(),
            "questionanswer" => new QuestionAnswerFormatter(),
            "alpaca" => new AlpacaFormatter(),
            "llama3" => new Llama3Formatter(),
            "raw" => new RawTemplateFormatter(),
            _ => null,
        };
    }
}

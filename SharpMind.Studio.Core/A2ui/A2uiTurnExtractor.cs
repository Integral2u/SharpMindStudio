namespace SharpMind.Studio.Core.A2ui;

public sealed record A2uiTurnResult(
    string Content,
    IReadOnlyList<A2uiDocument> Documents,
    bool Rejected);

/// <summary>
/// Converts fenced A2UI blocks in an assistant message into one document per
/// surface. One message may create or update several surfaces; each surface is
/// persisted as its own artifact so the transcript can render them independently.
/// </summary>
public static class A2uiTurnExtractor
{
    public static A2uiTurnResult Extract(string? content, string origin = "agent")
    {
        A2uiExtraction extraction = A2uiText.Extract(content);
        if (extraction.Blocks.Count == 0)
        {
            if (A2uiText.HasUnclosedBlock(content))
            {
                return new A2uiTurnResult(
                    A2uiText.RemoveOpenBlock(content),
                    [],
                    true);
            }
            return new A2uiTurnResult(content ?? "", [], false);
        }

        var documents = new List<A2uiDocument>();
        bool rejected = false;
        foreach (string block in extraction.Blocks)
        {
            A2uiParseResult parsed = A2uiParser.Parse(block);
            if (!parsed.Success)
            {
                rejected = true;
                continue;
            }

            var document = new A2uiDocument
            {
                Origin = origin,
                Messages = parsed.Messages,
            };
            A2uiValidationResult validation = A2uiValidator.Validate(document);
            if (!validation.Success)
            {
                rejected = true;
                continue;
            }

            foreach (IGrouping<string, A2uiEnvelope> group in parsed.Messages
                         .GroupBy(message => message.SurfaceId ?? ""))
            {
                if (group.Key.Length == 0) continue;
                documents.Add(new A2uiDocument
                {
                    Origin = origin,
                    Messages = [.. group],
                });
            }
        }

        return new A2uiTurnResult(extraction.Content, documents, rejected);
    }
}

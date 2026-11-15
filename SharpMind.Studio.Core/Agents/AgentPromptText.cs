using System.Text;

namespace SharpMind.Studio.Core.Agents;

/// <summary>
/// Prompt sections that explain delegation to the parent model and bind a
/// delegated agent to its one-task brief. Kept here so the wording is shared by
/// the chat, work and nested-agent paths and can be tested without a model.
/// </summary>
public static class AgentPromptText
{
    public const string DelegatedTask =
        "## Delegated task\n" +
        "You are a specialist running one self-contained task for another agent. " +
        "You cannot see the parent's conversation and cannot ask the user questions. " +
        "Return a concise, self-contained answer the parent can use directly. " +
        "Do not delegate back to the parent or to yourself.";

    /// <summary>
    /// The parent's guidance. With saved agents it lists them and explains both
    /// tools; with none it only offers <c>spawn_agent</c>.
    /// </summary>
    public static string Parent(IReadOnlyList<StudioAgentDefinition> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var text = new StringBuilder();
        text.AppendLine("## Specialist agents");
        text.AppendLine(
            "You can hand a self-contained task to a specialist. The specialist cannot see " +
            "this conversation and cannot ask the user questions, so give it the complete " +
            "task and all the context it needs.");
        if (agents.Count > 0)
        {
            text.AppendLine(
                "- `delegate` runs one of the saved agents below with its own rules, model, " +
                "tools and permissions.");
            text.AppendLine(
                "- `spawn_agent` creates a temporary specialist when no saved agent fits; its " +
                "rules and tool list come from the call.");
        }
        else
        {
            text.AppendLine(
                "- `spawn_agent` creates a temporary specialist for one self-contained task; " +
                "its rules and tool list come from the call.");
        }

        text.AppendLine(
            "Delegate only when the specialist can do the work better than you can; otherwise " +
            "do it yourself. The specialist's reply is a report, not the user's answer: check " +
            "it and give the user the final response yourself.");

        if (agents.Count > 0)
        {
            text.AppendLine("Saved agents:");
            foreach (StudioAgentDefinition agent in agents)
            {
                string purpose = string.IsNullOrWhiteSpace(agent.Description)
                    ? agent.Name
                    : $"{agent.Name}: {agent.Description}";
                text.AppendLine($"- {purpose}");
            }
        }

        return text.ToString().TrimEnd();
    }
}

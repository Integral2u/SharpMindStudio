namespace SharpMind.Studio.Core.A2ui;

/// <summary>The compact prompt inserted into Work sessions when A2UI is on.
/// It intentionally describes a small catalog instead of the full basic
/// catalog so a local model is not asked to retain thousands of tokens of
/// component schemas.</summary>
public static class A2uiPrompt
{
    public const string Text = """
        ## Interactive interfaces (A2UI 1.0)
        You can render a small interactive interface when the user must choose,
        fill in a form, or review structured results. Do not use an interface for
        ordinary prose, code, or a simple answer. Never use an interface to ask for
        tool permissions or to imitate a Studio security dialog.

        Output exactly one fenced ```a2ui block containing a JSON array of A2UI 1.0
        messages, followed by at most one short sentence. Do not put markdown,
        explanations, or code fences inside the block.

        Use the basic catalog id:
        https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json

        Supported components: Text, Column, Row, Card, Divider, List, Button,
        TextField, CheckBox, ChoicePicker. The first component must have id "root".
        Component ids use letters, digits and underscores, and start with a letter
        or underscore. Bind values with {"@path":"/json/pointer"}. A button action
        is {"event":{"name":"action_name","context":{"field":{"@path":"/path"}}}}.
        Do not use scripts, HTML, external URLs, custom catalogs, or more than 100
        components.

        Minimal shape:
        ```a2ui
        [
          {"version":"1.0","createSurface":{"surfaceId":"form","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","sendDataModel":true,"dataModel":{"name":""}}},
          {"version":"1.0","updateComponents":{"surfaceId":"form","components":[
            {"id":"root","component":"Column","children":["name","submit"]},
            {"id":"name","component":"TextField","label":"Name","value":{"@path":"/name"}},
            {"id":"submit","component":"Button","child":"submit_text","action":{"event":{"name":"submit","context":{"name":{"@path":"/name"}}}}},
            {"id":"submit_text","component":"Text","text":"Continue"}
          ]}}
        ]
        ```
        """;
}

using System.Text;
using System.Text.Json.Nodes;

// A minimal stdio MCP server for the integration tests. It speaks newline
// delimited JSON-RPC on stdin/stdout, like a real server, and exposes enough
// behaviour to test the client: a normal tool, an isError result, a JSON-RPC
// error, a notification interleaved with a response, and a process that dies
// in the middle of a call.

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

while (Console.ReadLine() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line) as JsonObject;
    }
    catch
    {
        continue;
    }

    if (request is null) continue;

    string? method = request["method"]?.GetValue<string>();
    if (method is null) continue;

    JsonNode? id = request["id"];

    switch (method)
    {
        case "initialize":
            Reply(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["resources"] = new JsonObject(),
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "fake-mcp",
                    ["version"] = "1.0.0",
                },
            });
            break;

        case "notifications/initialized":
            break;

        case "tools/list":
            // A notification before the response: the client must skip it.
            Write(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new JsonObject { ["level"] = "info", ["data"] = "listing tools" },
            });

            Reply(id, new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    Tool("echo", "Echoes the text argument", "text"),
                    Tool("env", "Returns an environment variable", "name"),
                    Tool("argv", "Returns the process command line", null),
                    Tool("fail", "Returns an isError result", null),
                    Tool("error", "Returns a JSON-RPC error", null),
                    Tool("crash", "Writes to stderr and exits", null),
                    A2uiTool("a2ui", "Returns an A2UI surface", "prompt"),
                    Tool("a2ui_action", "Handles an A2UI action", "name"),
                },
            });
            break;

        case "resources/list":
            Reply(id, new JsonObject
            {
                ["resources"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = "a2ui://card",
                        ["name"] = "A2UI card template",
                        ["mimeType"] = "application/a2ui+json",
                    },
                },
            });
            break;

        case "resources/read":
            Reply(id, new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = "a2ui://card",
                        ["mimeType"] = "application/a2ui+json",
                        ["text"] = A2uiTemplate(),
                    },
                },
            });
            break;

        case "tools/call":
            CallTool(id, request["params"] as JsonObject);
            break;

        default:
            Respond(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Unknown method '{method}'.",
                },
            });
            break;
    }
}

static void CallTool(JsonNode? id, JsonObject? parameters)
{
    string name = parameters?["name"]?.GetValue<string>() ?? "";
    JsonObject arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

    switch (name)
    {
        case "echo":
            Reply(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = arguments["text"]?.GetValue<string>() ?? "echo",
                    },
                },
            });
            break;

        case "fail":
            Reply(id, new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "tool failed" },
                },
            });
            break;

        case "env":
            string variable = arguments["name"]?.GetValue<string>() ?? "";
            Reply(id, Text(Environment.GetEnvironmentVariable(variable) ?? ""));
            break;

        case "argv":
            Reply(id, Text(string.Join("|", Environment.GetCommandLineArgs())));
            break;

        case "error":
            Respond(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32000,
                    ["message"] = "deliberate failure",
                },
            });
            break;

        case "crash":
            string leak = Environment.GetEnvironmentVariable("LEAK_VALUE") ?? "fake server crashed";
            Console.Error.WriteLine($"fake server crashed: {leak}");
            Console.Error.Flush();
            // Give the client's stderr drain a moment to see the line.
            Thread.Sleep(300);
            Environment.Exit(3);
            break;

        case "a2ui":
            Reply(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Rendered an A2UI card.",
                    },
                    new JsonObject
                    {
                        ["type"] = "resource",
                        ["resource"] = new JsonObject
                        {
                            ["uri"] = "a2ui://card/data",
                            ["mimeType"] = "application/a2ui+json",
                            ["text"] = A2uiUpdate(),
                        },
                    },
                },
                ["_meta"] = new JsonObject
                {
                    ["ui"] = new JsonObject
                    {
                        ["resourceUri"] = "a2ui://card",
                        ["mimeType"] = "application/a2ui+json",
                    },
                },
            });
            break;

        case "a2ui_action":
            Reply(id, Text(
                $"action {arguments["name"]?.GetValue<string>() ?? ""} on "
                + $"{arguments["surfaceId"]?.GetValue<string>() ?? ""}"));
            break;

        default:
            Respond(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32602,
                    ["message"] = $"Unknown tool '{name}'.",
                },
            });
            break;
    }
}

static JsonObject Text(string value) => new()
{
    ["content"] = new JsonArray
    {
        new JsonObject { ["type"] = "text", ["text"] = value },
    },
};

static JsonObject Tool(string name, string description, string? argument) => new()
{
    ["name"] = name,
    ["description"] = description,
    ["inputSchema"] = argument is null
        ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
        : new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [argument] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(argument),
        },
};

static JsonObject A2uiTool(string name, string description, string argument)
{
    JsonObject tool = Tool(name, description, argument);
    tool["_meta"] = new JsonObject
    {
        ["ui"] = new JsonObject
        {
            ["resourceUri"] = "a2ui://card",
            ["mimeType"] = "application/a2ui+json",
        },
    };
    return tool;
}

static string A2uiTemplate() => """
    [
      {
        "version": "1.0",
        "createSurface": {
          "surfaceId": "fake_card",
          "catalogId": "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json",
          "sendDataModel": true
        }
      },
      {
        "version": "1.0",
        "updateComponents": {
          "surfaceId": "fake_card",
          "components": [
            { "id": "root", "component": "Card", "child": "column" },
            { "id": "column", "component": "Column", "children": [ "title", "confirm" ] },
            { "id": "title", "component": "Text", "text": { "@path": "/title" } },
            {
              "id": "confirm",
              "component": "Button",
              "child": "confirm_text",
              "action": { "event": { "name": "confirm", "context": { "title": { "@path": "/title" } } } }
            },
            { "id": "confirm_text", "component": "Text", "text": "Confirm" }
          ]
        }
      }
    ]
    """;

static string A2uiUpdate() => """
    [
      {
        "version": "1.0",
        "updateDataModel": {
          "surfaceId": "fake_card",
          "path": "/",
          "value": { "title": "Fake card" }
        }
      }
    ]
    """;

static void Reply(JsonNode? id, JsonNode result) =>
    Respond(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    });

static void Respond(JsonObject message) => Write(message);

static void Write(JsonObject message)
{
    Console.Out.WriteLine(message.ToJsonString());
    Console.Out.Flush();
}

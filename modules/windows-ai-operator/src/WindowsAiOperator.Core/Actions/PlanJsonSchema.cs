using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsAiOperator.Core.Actions;

/// <summary>
/// JSON Schema of a plan, sent as <c>response_format</c> to servers that support structured output (Strata validates
/// it, llama.cpp turns it into a grammar). The executor validates the answer again with <see cref="PlanParser"/>.
/// </summary>
public static class PlanJsonSchema
{
    public const string SchemaName = "operator_plan";

    public static JsonObject Build(bool allowClickPoint, int maxSteps)
    {
        var actions = new JsonArray();
        foreach (var descriptor in ActionCatalog.All)
        {
            if (descriptor.Type == ActionType.ClickPoint && !allowClickPoint)
            {
                continue;
            }

            actions.Add(descriptor.WireName);
        }

        var step = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("action"),
            ["properties"] = new JsonObject
            {
                ["id"] = Int(1, 9999),
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = actions },
                ["description"] = Str(200),
                ["app"] = Str(64),
                ["window"] = Obj(new JsonObject
                {
                    ["app"] = Str(64),
                    ["title_contains"] = Str(200),
                    ["handle"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                }),
                ["element"] = Obj(new JsonObject
                {
                    ["ref"] = new JsonObject { ["type"] = "string", ["pattern"] = "^e[0-9]{1,6}$" },
                    ["automation_id"] = Str(200),
                    ["name"] = Str(200),
                    ["name_contains"] = Str(200),
                    ["control_type"] = Str(40),
                    ["class_name"] = Str(200),
                    ["index"] = Int(0, 50),
                }),
                ["text"] = Str(4000),
                ["mode"] = Enum("replace", "append"),
                ["checked"] = new JsonObject { ["type"] = "boolean" },
                ["menu_path"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6, ["items"] = Str(100) },
                ["direction"] = Enum("up", "down", "left", "right"),
                ["amount"] = Int(1, 20),
                ["keys"] = Str(40),
                ["point"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("x", "y"),
                    ["properties"] = new JsonObject
                    {
                        ["x"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1000 },
                        ["y"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1000 },
                    },
                },
                ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["path"] = Str(260),
                ["timeout_ms"] = Int(100, 60000),
                ["delay_ms"] = Int(0, 10000),
                ["message"] = Str(2000),
                ["expect"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["value_equals"] = Str(4000),
                        ["value_contains"] = Str(4000),
                        ["name_contains"] = Str(200),
                        ["checked"] = new JsonObject { ["type"] = "boolean" },
                        ["selected"] = new JsonObject { ["type"] = "boolean" },
                        ["exists"] = new JsonObject { ["type"] = "boolean" },
                        ["gone"] = new JsonObject { ["type"] = "boolean" },
                        ["window_title_contains"] = Str(200),
                    },
                },
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("steps"),
            ["properties"] = new JsonObject
            {
                ["summary"] = Str(300),
                ["target_app"] = Str(64),
                ["status"] = Enum("ok", "need_user_input", "cannot_do"),
                ["message"] = Str(2000),
                ["steps"] = new JsonObject { ["type"] = "array", ["maxItems"] = maxSteps, ["items"] = step },
            },
        };
    }

    public static string BuildText(bool allowClickPoint, int maxSteps) =>
        Build(allowClickPoint, maxSteps).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static JsonObject Str(int max) => new() { ["type"] = "string", ["maxLength"] = max };

    private static JsonObject Int(int min, int max) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };

    private static JsonObject Enum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return new JsonObject { ["type"] = "string", ["enum"] = array };
    }

    private static JsonObject Obj(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = properties,
    };
}

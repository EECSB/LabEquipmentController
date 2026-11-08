using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabEquipmentController;

/// <summary>What kind of value one argument of a tool takes.</summary>
public enum ToolValue
{
    Text,
    Number,
    Flag,
}

/// <summary>
/// One argument a tool takes: its name, what it holds, what it is for, and whether it must be
/// given. The schema a model is shown is written from a list of these, and a call is read
/// against the same list — so the two cannot disagree, which is the first of GDS Viewer's rules
/// for tools that work across providers.
/// </summary>
public sealed record ToolParameter(
    string Name,
    ToolValue Kind,
    string Description,
    bool Required = false,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>
    /// The JSON Schema for an object of these arguments, in the only keywords every provider
    /// reads — type, description, properties, required, enum. Google's function calling takes a
    /// subset of OpenAPI and refuses <c>additionalProperties</c>, <c>oneOf</c> and
    /// <c>default</c> outright, so a schema using them works with one model and is an error with
    /// another.
    /// </summary>
    public static string SchemaFor(IReadOnlyList<ToolParameter> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (ToolParameter p in parameters)
        {
            var property = new JsonObject
            {
                ["type"] = p.Kind switch
                {
                    ToolValue.Number => "number",
                    ToolValue.Flag => "boolean",
                    _ => "string",
                },
                ["description"] = p.Description,
            };
            if (p.Choices is { Count: > 0 })
                property["enum"] = new JsonArray([.. p.Choices.Select(c => (JsonNode)JsonValue.Create(c)!)]);
            properties[p.Name] = property;
            if (p.Required) required.Add(p.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema.ToJsonString();
    }
}

/// <summary>
/// The arguments of one call, read against what the tool takes.
///
/// Strict about names, forgiving about spelling — GDS Viewer's rule, and for the same reason. An
/// argument the tool does not take is refused with the ones it does: a model writing
/// <c>channel</c> where the tool says <c>instrument</c> would otherwise be answered about the
/// wrong instrument, or a default, with nobody knowing why. But a number sent as <c>"1.5"</c>, a
/// flag as <c>"yes"</c>, and the whole object sent as a JSON string — all things some model does —
/// are read as what was meant.
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonObject _values;

    private ToolArguments(JsonObject values) => _values = values;

    /// <summary>
    /// Reads a call's arguments, or says what is wrong with them in a sentence the model can act
    /// on — with <paramref name="tool"/> naming the call it is about.
    /// </summary>
    public static ToolArguments? Read(string? json, string tool, IReadOnlyList<ToolParameter> parameters, out string? problem)
    {
        problem = null;
        JsonObject values;
        try
        {
            JsonNode? node = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json);

            // The whole object, sent as a string of JSON: read the string.
            if (node is JsonValue v && v.TryGetValue(out string? inner))
                node = string.IsNullOrWhiteSpace(inner) ? new JsonObject() : JsonNode.Parse(inner);

            if (node is not JsonObject o)
            {
                problem = $"{tool} takes an object of named arguments, and was given {Describe(node)}.";
                return null;
            }
            values = o;
        }
        catch (JsonException)
        {
            problem = $"{tool}'s arguments are not JSON. Send them as an object: {Expected(parameters)}.";
            return null;
        }

        var unknown = values.Select(kv => kv.Key)
                            .Where(name => !parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
        if (unknown.Count > 0)
        {
            problem = $"{tool} takes no {Join(unknown)}. It takes {Expected(parameters)}.";
            return null;
        }

        var read = new ToolArguments(values);
        foreach (ToolParameter p in parameters)
        {
            JsonNode? given = read.Find(p.Name);
            bool missing = given is null || (given is JsonValue s && s.TryGetValue(out string? text) && string.IsNullOrWhiteSpace(text));
            if (missing)
            {
                if (p.Required)
                {
                    problem = $"{tool} needs {p.Name}: {p.Description}";
                    return null;
                }
                continue;
            }

            string? wrong = p.Kind switch
            {
                ToolValue.Number when read.Number(p.Name) is null => $"{p.Name} is a number, and was given {Describe(given)}.",
                ToolValue.Flag when read.FlagOrNull(p.Name) is null => $"{p.Name} is true or false, and was given {Describe(given)}.",
                ToolValue.Text when p.Choices is { Count: > 0 } && !p.Choices.Contains(read.Text(p.Name), StringComparer.OrdinalIgnoreCase)
                    => $"{p.Name} is one of {Join(p.Choices)}, and was given {Describe(given)}.",
                _ => null,
            };
            if (wrong is not null)
            {
                problem = $"{tool}: {wrong}";
                return null;
            }
        }
        return read;
    }

    /// <summary>The text given for <paramref name="name"/>, trimmed, or null. A number or a flag given there is read as its text.</summary>
    public string? Text(string name) => Find(name) switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s.Trim(),
        JsonValue v => v.ToJsonString(),
        null => null,
        var other => other.ToJsonString(),
    };

    /// <summary>The number given for <paramref name="name"/>, or null — a number written as text included.</summary>
    public double? Number(string name)
    {
        if (Find(name) is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue(out string? s)
            && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed))
            return parsed;
        return null;
    }

    /// <summary>The flag given for <paramref name="name"/>, false when it was not given.</summary>
    public bool Flag(string name) => FlagOrNull(name) ?? false;

    private bool? FlagOrNull(string name)
    {
        if (Find(name) is not JsonValue v) return null;
        if (v.TryGetValue(out bool b)) return b;
        if (v.TryGetValue(out double d)) return d != 0;
        if (v.TryGetValue(out string? s))
        {
            return s.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" or "on" => true,
                "false" or "no" or "0" or "off" => false,
                _ => null,
            };
        }
        return null;
    }

    // The name as the model spelled it: case is the one liberty taken, since "Instrument" for
    // "instrument" is never a different argument.
    private JsonNode? Find(string name)
        => _values.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Expected(IReadOnlyList<ToolParameter> parameters)
        => parameters.Count == 0 ? "no arguments" : Join(parameters.Select(p => p.Name).ToList());

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "nothing",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static string Describe(JsonNode? node)
    {
        string text = node?.ToJsonString() ?? "nothing";
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}

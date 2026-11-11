using System.Globalization;

namespace DotNetLab.LichessBridge.Engine;

internal sealed record UciOption(string Name, string Type, int? Minimum, int? Maximum, IReadOnlyList<string> Choices)
{
    public static UciOption Parse(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 5 || fields[1] is not "name")
        {
            throw new InvalidDataException($"Invalid UCI option declaration: '{line}'.");
        }

        var typeIndex = Array.IndexOf(fields, "type", 2);

        if (typeIndex < 3 || typeIndex + 1 >= fields.Length)
        {
            throw new InvalidDataException($"Invalid UCI option declaration: '{line}'.");
        }

        var name = string.Join(' ', fields[2..typeIndex]);
        var choices = new List<string>();
        int? minimum = null;
        int? maximum = null;

        for (var index = typeIndex + 2; index < fields.Length; index++)
        {
            if (fields[index] is "min" or "max" && index + 1 < fields.Length && int.TryParse(fields[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
            {
                if (fields[index] is "min")
                {
                    minimum = limit;
                }
                else
                {
                    maximum = limit;
                }

                index++;
            }
            else if (fields[index] is "var")
            {
                var start = ++index;

                while (index < fields.Length && fields[index] is not "var")
                {
                    index++;
                }

                choices.Add(string.Join(' ', fields[start..index]));
                index--;
            }
        }

        return new UciOption(name, fields[typeIndex + 1], minimum, maximum, choices);
    }

    public string FormatCommand(string value)
    {
        var valid = Type switch
        {
            "check" => bool.TryParse(value, out _),
            "spin" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum),
            "combo" => Choices.Contains(value, StringComparer.OrdinalIgnoreCase),
            "button" => value.Length is 0,
            "string" => true,
            _ => false
        };

        if (valid is false || value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidOperationException($"Invalid value for UCI option '{Name}' (type {Type}, min {Minimum}, max {Maximum}).");
        }

        if (Type is "button")
        {
            return $"setoption name {Name}";
        }

        var formattedValue = Type is "check" ? value.ToLowerInvariant() : value.Length is 0 ? "<empty>" : value;

        return $"setoption name {Name} value {formattedValue}";
    }
}

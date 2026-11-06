using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SousChef.Core;

internal static class CapabilityTests
{
    public static int Run()
    {
        int checks = 0;
        using (var document = JsonDocument.Parse(File.ReadAllText("tests/fixtures/capability-wire.json")))
        foreach (var fixture in document.RootElement.EnumerateArray())
        {
            var command = ControlCommand.Parse(fixture.GetProperty("wire").GetString());
            var task = command.Task;
            var actual = Json.Object("operation", command.Operation, "destination", command.Destination,
                "source", command.Source, "selector", command.Selector, "condition", command.Condition,
                "milliseconds", command.Milliseconds, "desired_state", command.DesiredState,
                "task", task == null ? null : task.Action, "player", task == null ? 0 : task.Player,
                "repetitions", task == null ? 0 : task.Repetitions,
                "items", task == null ? null : task.Items.Select(item => Json.Object("ingredient", item.Selector, "preparation", item.Preparation, "quantity", item.Quantity)).ToArray(),
                "order", task == null || task.Assembly == null ? null : task.Assembly.Order,
                "bases", task == null || task.Assembly == null ? null : task.Assembly.Bases,
                "components", task == null || task.Assembly == null ? null : task.Assembly.Components,
                "equipment", task == null || task.Assembly == null ? null : task.Assembly.Equipment);
            using (var parsed = JsonDocument.Parse(Json.Encode(actual)))
            foreach (var field in fixture.GetProperty("native").EnumerateObject())
            {
                if (JsonSerializer.Serialize(parsed.RootElement.GetProperty(field.Name)) != JsonSerializer.Serialize(field.Value))
                    throw new Exception("Contract/native wire mismatch: " + fixture.GetProperty("name") + "." + field.Name);
                checks++;
            }
        }
        return checks;
    }
}

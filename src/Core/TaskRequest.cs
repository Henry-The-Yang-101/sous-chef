using System;
using System.Collections.Generic;
using System.Globalization;

namespace SousChef.Core
{
    public sealed class PreparationRequest
    {
        public string Selector, Preparation;
        public int Quantity;
    }
    public sealed class TaskRequest
    {
        public static readonly string[] Names = CapabilityRegistry.Tasks;
        public string Action;
        public string Target, DesiredState = "trigger";
        public int Repetitions = 1, Player = 1; // Zero means until explicitly stopped.
        public readonly List<PreparationRequest> Items = new List<PreparationRequest>();
        public AssemblyRequest Assembly;
        internal static TaskRequest Parse(string[] tokens)
        {
            if (tokens.Length < 4 || Array.IndexOf(Names, tokens[2]) < 0) throw new ArgumentException("invalid_task");
            var task = new TaskRequest { Action = tokens[2] };
            if (tokens[3] == "forever") task.Repetitions = 0;
            else if (!Int32.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out task.Repetitions) || task.Repetitions < 1 || task.Repetitions > CapabilityRegistry.MaxRepetitions)
                throw new ArgumentException("invalid_repetitions");
            if (task.Action == "press_button")
            {
                if (task.Repetitions != 1) throw new ArgumentException("activation_requires_one_request");
                if (tokens.Length != 6 || !ControlCommand.DestinationId(tokens[4])) throw new ArgumentException("invalid_destination");
                if (!ControlCommand.ActivationState(tokens[5])) throw new ArgumentException("invalid_state");
                task.Target = tokens[4]; task.DesiredState = tokens[5]; return task;
            }
            if (task.Action == "assemble_dish")
            {
                if (task.Repetitions != 1) throw new ArgumentException("assembly_requires_one_ticket");
                task.Assembly = AssemblyRequest.Parse(tokens); return task;
            }
            if (task.Action != "prepare_near_player")
            { if (tokens.Length != 4) throw new ArgumentException("invalid_task_arguments"); return task; }
            if (tokens.Length < 8 || tokens.Length > 5 + CapabilityRegistry.MaxPreparationItems * 3 || (tokens.Length - 5) % 3 != 0 || (tokens[4] != "1" && tokens[4] != "3" && tokens[4] != "4"))
                throw new ArgumentException("invalid_preparation_list");
            task.Player = Int32.Parse(tokens[4], CultureInfo.InvariantCulture);
            for (int i = 5; i < tokens.Length; i += 3)
            {
                int quantity;
                if (Array.IndexOf(CapabilityRegistry.Preparations, tokens[i + 1]) < 0 ||
                    !Int32.TryParse(tokens[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity < 1 || quantity > CapabilityRegistry.MaxQuantity)
                    throw new ArgumentException("invalid_preparation");
                task.Items.Add(new PreparationRequest { Selector = ControlCommand.DecodeSelector(tokens[i]), Preparation = tokens[i + 1], Quantity = quantity });
            }
            return task;
        }
    }
    public sealed class AssemblyRequest
    {
        public string Order;
        public readonly List<string> Bases = new List<string>(), Components = new List<string>(), Equipment = new List<string>();
        internal static AssemblyRequest Parse(string[] tokens)
        {
            if (tokens.Length < 9) throw new ArgumentException("invalid_assembly_allocation");
            var result = new AssemblyRequest { Order = ControlCommand.DecodeSelector(tokens[4]) };
            int cursor = 5; var seen = new HashSet<string>();
            foreach (var list in new[] { result.Bases, result.Components, result.Equipment })
            {
                int count;
                if (cursor >= tokens.Length || !Int32.TryParse(tokens[cursor++], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                    count < (list == result.Bases ? 1 : 0) || count > CapabilityRegistry.MaxAllocationGroup || cursor + count > tokens.Length)
                    throw new ArgumentException("invalid_assembly_allocation");
                for (int i = 0; i < count; i++)
                {
                    string id = tokens[cursor++];
                    if (id.Length < 2 || id.Length > 32 || id[0] != 'd' || !seen.Add(id)) throw new ArgumentException("invalid_assembly_resource");
                    for (int c = 1; c < id.Length; c++) if (id[c] < '0' || id[c] > '9') throw new ArgumentException("invalid_assembly_resource");
                    list.Add(id);
                }
            }
            if (cursor != tokens.Length) throw new ArgumentException("invalid_assembly_allocation");
            return result;
        }
    }
}

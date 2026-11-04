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
        public static readonly string[] Names = { "prepare_near_player", "take_ready", "wash_dishes", "deliver_dirty_dishes", "put_out_fire", "toss_burnt_food", "monitor_equipment" };
        public string Action;
        public int Repetitions = 1, Player = 1; // Zero means until explicitly stopped.
        public readonly List<PreparationRequest> Items = new List<PreparationRequest>();
        internal static TaskRequest Parse(string[] tokens)
        {
            if (tokens.Length < 4 || Array.IndexOf(Names, tokens[2]) < 0) throw new ArgumentException("invalid_task");
            var task = new TaskRequest { Action = tokens[2] };
            if (tokens[3] == "forever") task.Repetitions = 0;
            else if (!Int32.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out task.Repetitions) || task.Repetitions < 1 || task.Repetitions > 10)
                throw new ArgumentException("invalid_repetitions");
            if (task.Action != "prepare_near_player")
            { if (tokens.Length != 4) throw new ArgumentException("invalid_task_arguments"); return task; }
            if (tokens.Length < 8 || tokens.Length > 35 || (tokens.Length - 5) % 3 != 0 || (tokens[4] != "1" && tokens[4] != "3" && tokens[4] != "4"))
                throw new ArgumentException("invalid_preparation_list");
            task.Player = Int32.Parse(tokens[4], CultureInfo.InvariantCulture);
            for (int i = 5; i < tokens.Length; i += 3)
            {
                int quantity;
                if (Array.IndexOf(new[] { "raw", "chopped", "cooked", "mixed", "empty" }, tokens[i + 1]) < 0 ||
                    !Int32.TryParse(tokens[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity < 1 || quantity > 10)
                    throw new ArgumentException("invalid_preparation");
                task.Items.Add(new PreparationRequest { Selector = ControlCommand.DecodeSelector(tokens[i]), Preparation = tokens[i + 1], Quantity = quantity });
            }
            return task;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    internal sealed partial class TaskActions
    {
        partial void RegisterMonitor() { Register("monitor_equipment", MonitorAvailable, MonitorEquipment); }
        private bool MonitorAvailable()
        {
            inventory.Refresh(chef);
            foreach (GameObject target in navigation.Targets()) if (TaskNative.Equipment(target) && Reachable(target)) return true;
            return false;
        }
        private IEnumerable<TaskStep> MonitorEquipment()
        {
            while (MoreRequested)
            {
                GameObject station = null;
                // Observe every accessible processor. Binding to the first unfinished
                // pot would miss a blender/stove that finishes earlier elsewhere.
                yield return Until("monitoring_equipment", delegate { station = ProcessingStation(true, true); return station != null; });
                GameObject item = ActionObservations.Receiver(station);
                foreach (TaskStep step in RemoveReady(station, item)) yield return step;
                completed++;
            }
        }
    }
}

using System.Collections.Generic;
using SousChef.Core;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // One ticket, finite assigned objects, and a persistent selected base. Unlike
    // preparation lanes, an addition satisfies part of a recipe, not a handoff.
    internal sealed class AssemblySchedule
    {
        internal sealed class Stage
        {
            public AssembledDefinitionNode Goal, Inputs;
            public Stage Parent;
            public GameObject Vessel;
            public readonly List<Stage> Children = new List<Stage>();
            public bool Processing { get { return Parent != null; } }
            public bool Mixing { get { return Goal is MixedCompositeAssembledNode; } }
        }
        public AssemblyOrder Order;
        public readonly List<GameObject> Bases = new List<GameObject>(), Components = new List<GameObject>(), Equipment = new List<GameObject>();
        public GameObject Base;
        public long Additions;
        public string Waiting;
        public bool Verified;
        public readonly Dictionary<GameObject, float> RetryAt = new Dictionary<GameObject, float>();
        public Stage Root;
        public readonly List<Stage> Stages = new List<Stage>();
        public void Initialize()
        {
            Root = new Stage { Goal = Order.Goal, Inputs = Order.Goal };
            Build(Root);
        }
        private void Build(Stage stage)
        {
            Stages.Add(stage);
            foreach (var part in RecipeProof.Parts(stage.Inputs))
            {
                var process = part as CompositeAssembledNode;
                if (!(process is CookedCompositeAssembledNode) && !(process is MixedCompositeAssembledNode)) continue;
                var child = new Stage { Parent = stage, Goal = process,
                    Inputs = new CompositeAssembledNode { m_composition = process.m_composition } };
                stage.Children.Add(child); Build(child);
            }
        }
        public object Describe()
        {
            var stages = new List<object>();
            foreach (var stage in Stages) stages.Add(Json.Object("operation", stage.Processing ? stage.Mixing ? "mix" : "cook" : "assemble",
                "vesselInstanceId", stage.Vessel == null ? null : (object)stage.Vessel.GetInstanceID()));
            return Json.Object("orderId", Order.Id, "recipe", Order.Recipe.name, "baseInstanceId", Base == null ? null : (object)Base.GetInstanceID(),
                "verifiedAdditions", Additions, "compositionVerified", Verified, "waitingReason", Waiting, "stages", stages);
        }
    }
}

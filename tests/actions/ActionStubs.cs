using System;
using System.Collections.Generic;
using UnityEngine;

public static class GameObjectUtils
{
    public static T RequestInterface<T>(GameObject item) where T : class { return item.GetComponent<T>(); }
}
public class AssembledDefinitionNode
{
    public string Name;
    public AssembledDefinitionNode(string name) { Name = name; }
    public static bool Matching(AssembledDefinitionNode a, AssembledDefinitionNode b) { return a != null && b != null && a.Name == b.Name; }
}
public interface IOrderDefinition { AssembledDefinitionNode GetOrderComposition(); }
public class IngredientPropertiesComponent : MonoBehaviour, IOrderDefinition
{
    public AssembledDefinitionNode Node;
    public UnityEngine.Object m_ingredientOrderNode;
    public AssembledDefinitionNode GetOrderComposition() { return Node; }
}
public class Definition : UnityEngine.Object { public int m_uID; }
public class PreparationContainer : MonoBehaviour { public UnityEngine.Object m_ingredientOrderNode; }
public class ServerConveyorStation : MonoBehaviour { }
public class ItemPropertiesComponent : MonoBehaviour { public UnityEngine.Object m_itemDefinition; }
public class CleanPlateStack : MonoBehaviour { }
public interface IContainerTransferBehaviour { bool CanTransferToContainer(ServerIngredientContainer target); }
public class IngredientTransfer : MonoBehaviour, IContainerTransferBehaviour { public bool CanTransferToContainer(ServerIngredientContainer target) { return true; } }
public class ServerIngredientContainer : MonoBehaviour
{
    public readonly List<AssembledDefinitionNode> Contents = new List<AssembledDefinitionNode>();
    public int GetContentsCount() { return Contents.Count; }
    public AssembledDefinitionNode[] GetContents() { return Contents.ToArray(); }
    public bool CanAddIngredient(AssembledDefinitionNode node) { return Contents.Count < 5; }
}
public class ServerPlayerAttachmentCarrier : MonoBehaviour { public GameObject Item; public GameObject InspectCarriedItem() { return Item; } }
public class ServerAttachStation : MonoBehaviour
{
    public GameObject Item;
    public GameObject InspectItem() { return Item; }
    public bool CanAttachToSelf(GameObject item,PlacementContext context) { return Item==null; }
}
public class ClientAttachStation : MonoBehaviour, IPlacement
{
    public GameObject Item;
    public readonly List<Func<GameObject,PlacementContext,bool>> m_allowPlacementCallbacks = new List<Func<GameObject,PlacementContext,bool>>();
    public GameObject InspectItem() { return Item; }
    public bool CanHandlePlacement(ICarrier carrier,Vector2 direction,PlacementContext context) { return false; }
}
public class ServerStack : MonoBehaviour
{
    public List<GameObject> m_stackItems = new List<GameObject>();
    public int GetSize() { return m_stackItems.Count; }
    public GameObject InspectTopOfStack() { return m_stackItems.Count==0 ? null : m_stackItems[m_stackItems.Count-1]; }
}
public class PlatingStepData : UnityEngine.Object { }
public class ServerPlate : MonoBehaviour
{
    public ServerIngredientContainer m_ingredientContainer;
    public AssembledDefinitionNode Composition;
    public PlatingStepData PlatingStep;
    public bool Reserved;
    public bool IsReserved() { return Reserved; }
    public AssembledDefinitionNode GetOrderComposition() { return Composition; }
    public PlatingStepData GetPlatingStep() { return PlatingStep; }
}
public class PlateStationMessage { public GameObject m_delivered; public bool m_success; }
public class ServerPlateStation : MonoBehaviour
{
    public PlateStationMessage m_data = new PlateStationMessage();
    public int Team;
    public int GetTeamID() { return Team; }
}
namespace OrderController
{
    public struct OrderID { public int Value; }
    public class ServerOrderData { public OrderID ID; public float Remaining, Lifetime; }
}
public class ServerOrderControllerBase
{
    public readonly List<AssembledDefinitionNode> Recipes = new List<AssembledDefinitionNode>();
    public PlatingStepData PlatingStep;
    public readonly Dictionary<string, float> Remaining = new Dictionary<string, float>(), Lifetimes = new Dictionary<string, float>();
    public readonly List<OrderController.ServerOrderData> m_activeOrders = new List<OrderController.ServerOrderData>();
    public bool FindBestOrderForRecipe(AssembledDefinitionNode food, PlatingStepData plate, out OrderController.OrderID order, out float remaining)
    {
        order = new OrderController.OrderID(); remaining = 1;
        if (plate != PlatingStep || !Recipes.Exists(recipe => AssembledDefinitionNode.Matching(recipe, food))) return false;
        float seconds, lifetime;
        if (!Remaining.TryGetValue(food.Name, out seconds)) seconds = 100;
        if (!Lifetimes.TryGetValue(food.Name, out lifetime)) lifetime = 100;
        order.Value = food.Name.GetHashCode(); remaining = seconds / lifetime;
        m_activeOrders.Clear();m_activeOrders.Add(new OrderController.ServerOrderData {ID=order,Remaining=seconds,Lifetime=lifetime});
        return true;
    }
}
public class ServerTeamMonitor { public ServerOrderControllerBase OrdersController = new ServerOrderControllerBase(); }
public class ServerKitchenFlowControllerBase : MonoBehaviour
{
    public readonly ServerTeamMonitor Monitor = new ServerTeamMonitor();
    public ServerTeamMonitor GetMonitorForTeam(int team) { return team == 0 ? Monitor : null; }
}
public class ServerWashingStation : MonoBehaviour { public int m_plateCount; public float m_cleaningTimer; public ServerAttachStation m_dryingAttachStation; }
public class ClientTriggerToggleOnAnimator : MonoBehaviour { public bool CurrentState; }
public class ClientToggleSwitchCosmeticDecisions : MonoBehaviour { public ClientTriggerToggleOnAnimator m_toggleOnAnimator; }
public class ServerWorkableItem : MonoBehaviour { public bool Finished; public int m_subProgress; public float Progress; public bool HasFinished() { return Finished; } public float GetProgress() { return Progress; } }
public class WorkableItem : MonoBehaviour { public int m_stages=3; public GameObject Next; public GameObject GetNextPrefab() { return Next; } }
public class ServerRubbishBin : MonoBehaviour { }
public class ServerInteractable : MonoBehaviour
{
    public delegate void BeginInteractCallback(GameObject actor,Vector2 direction);
    public BeginInteractCallback Triggered;
    public void RegisterTriggerCallbacks(BeginInteractCallback callback) { Triggered += callback; }
    public void UnregisterTriggerCallbacks(BeginInteractCallback callback) { Triggered -= callback; }
}
public class ClientCookingHandler : MonoBehaviour { public object m_cachedCookingState = "Raw"; }
public class ClientMixingHandler : MonoBehaviour { public float m_cachedMixingProgress; public object m_cachedMixingState="Idle"; }
public interface IClientThrowable { bool CanHandleThrow(ClientAttachmentThrower thrower,Vector2 direction); }
public class ClientAttachmentThrower : MonoBehaviour { }
public class AttachmentThrower : MonoBehaviour { public float m_throwForce=8,m_throwInclination=15; }
public class ServerAttachmentThrower : MonoBehaviour
{
    public int Calls;
    public Func<Vector2,Vector3> Override;
    public GenericVoid<GameObject> Launched;
    public void RegisterThrowCallback(GenericVoid<GameObject> callback) { Launched+=callback; }
    public void UnregisterThrowCallback(GenericVoid<GameObject> callback) { Launched-=callback; }
    private Vector3 CalculateThrowVelocity(Vector2 direction)
    {
        Calls++;if(Override!=null)return Override(direction);
        var data=GetComponent<AttachmentThrower>();return new Vector3(direction.x*data.m_throwForce,data.m_throwForce*Mathf.Tan(data.m_throwInclination*Mathf.Deg2Rad),direction.y*data.m_throwForce);
    }
}
public delegate void GenericVoid<T>(T value);
public class AttachmentWindReceiver : WindAccumulator { }
public class ServerAttachmentWindReceiver : MonoBehaviour { }
public static class TimeManager { public static float Scale=1;public static float GetDeltaTime(GameObject item) { return Time.deltaTime*Scale; } }
public class AttachmentCatcher : MonoBehaviour { public float m_catchDistance=.8f,m_catchAngleMax=60; }
public interface ICatchable { }
public interface IHandleCatch { bool CanHandleCatch(ICatchable item,Vector2 direction); }
public class ServerIngredientCatcher : MonoBehaviour, IHandleCatch
{
    public bool Allowed=true;
    public bool CanHandleCatch(ICatchable item,Vector2 direction) { return Allowed; }
}
public class ServerAttachmentCatchingProxy : MonoBehaviour { }
public class ClientPlayerControlsImpl_Default : MonoBehaviour
{
    public ClientInteractable Current;
    public ClientInteractable GetCurrentlyInteracting() { return Current; }
    private void EndInteraction() { Current=null; }
}
public static class ClientMessenger
{
    public static int Ends;
    public static void ChefEventMessage(Team17.Online.Multiplayer.Messaging.ChefEventMessage.ChefEventType kind,GameObject chef,MonoBehaviour target) { Ends++; }
}
namespace Team17.Online.Multiplayer.Messaging
{
    public class ChefEventMessage { public enum ChefEventType { Interact } }
}

public class PlacementLayerSwapper : MonoBehaviour { public int m_defaultLayerId=14; }

public enum ProcessingState { Idle, Progressing, Completed, OverDoing, Ruined }
public class CookingStateMessage { public ProcessingState m_cookingState; public float m_cookingProgress; }
public class MixingStateMessage { public ProcessingState m_mixingState; public float m_mixingProgress; }
public class ServerCookingHandler : MonoBehaviour
{
    public CookingStateMessage m_ServerData = new CookingStateMessage();
    public float AccessCookingTime { get { return 10; } }
    public float GetCookingProgress() { return m_ServerData.m_cookingProgress; }
}
public class ServerMixingHandler : MonoBehaviour
{
    public MixingStateMessage m_serverData = new MixingStateMessage();
    public float AccessMixingTime { get { return 10; } }
    public float GetMixingProgress() { return m_serverData.m_mixingProgress; }
}

public class ServerFlammable : MonoBehaviour
{
    public bool Burning;
    public bool OnFire() { return Burning; }
    public static IEnumerable<ServerFlammable> GetAllOnFire() { foreach(var fire in UnityEngine.Object.FindObjectsOfType<ServerFlammable>()) if(fire.Burning) yield return fire; }
}

public class ServerDirtyPlateStack : MonoBehaviour { }
public class ServerFireExtinguishSpray : MonoBehaviour { }

public class MixingStation : MonoBehaviour { }

public class CookingHandler : MonoBehaviour { public UnityEngine.Object m_cookingType; }
public class MixingHandler : MonoBehaviour { public UnityEngine.Object m_mixingType; }
public class CookableContainer : MonoBehaviour { }
public class MixableContainer : MonoBehaviour { }

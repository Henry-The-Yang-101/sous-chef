// Deliberately small test doubles for the exact Unity/game API consumed by the
// navigator. Tests run the production navigator, not a second implementation.
// Analytical floors and injectable collision queries do not emulate PhysX.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        private static int next;
        internal static readonly List<Object> All = new List<Object>();
        public Object() { All.Add(this); }
        public static int PlayerSearches;
        public static T[] FindObjectsOfType<T>() { if (typeof(T) == typeof(global::PlayerControls)) PlayerSearches++; return All.OfType<T>().ToArray(); }
        private readonly int id = ++next;
        public bool Destroyed;
        public static bool operator ==(Object a,Object b)
        {
            bool an=ReferenceEquals(a,null) || a.Destroyed,bn=ReferenceEquals(b,null) || b.Destroyed;
            return an || bn ? an==bn : ReferenceEquals(a,b);
        }
        public static bool operator !=(Object a,Object b) { return !(a==b); }
        public override bool Equals(object value) { return value is Object other && this==other; }
        public override int GetHashCode() { return id; }
        public int GetInstanceID() { return id; }
    }
    public class GameObject : Object
    {
        public string tag = "Untagged";
        public int layer;
        public bool activeInHierarchy = true;
        public readonly Transform transform;
        private readonly List<Component> components = new List<Component>();
        public GameObject(string label = "object") { name = label; transform = Add(new Transform()); }
        public T Add<T>(T component) where T : Component { component.gameObject = this; components.Add(component); return component; }
        public T GetComponent<T>() where T : class { return components.OfType<T>().FirstOrDefault(); }
        public T[] GetComponents<T>() { return components.OfType<T>().ToArray(); }
        public T[] GetComponentsInChildren<T>() { return Object.All.OfType<Component>().Where(c => c.gameObject != null && c.transform.IsChildOf(transform)).OfType<T>().ToArray(); }
        public T GetComponentInParent<T>() where T : class
        { for (Transform t = transform; t != null; t = t.parent) { T c = t.GetComponent<T>(); if (c != null) return c; } return null; }
        public bool CompareTag(string value) { return tag == value; }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : class { return gameObject.GetComponent<T>(); }
        public T[] GetComponents<T>() { return gameObject.GetComponents<T>(); }
        public T GetComponentInParent<T>() where T : class { return gameObject.GetComponentInParent<T>(); }
        public bool CompareTag(string value) { return gameObject.CompareTag(value); }
    }
    public class MonoBehaviour : Component { public bool enabled = true; public bool isActiveAndEnabled { get { return enabled && gameObject.activeInHierarchy; } } }
    public class Transform : Component
    {
        public Vector3 position, lossyScale = Vector3.one;
        public Vector3[] Basis;
        public float yaw;
        public Quaternion rotation { get { return new Quaternion {Yaw=yaw}; } }
        public Transform parent;
        public Vector3 up { get { return Vector3.up; } }
        public Vector3 forward { get { return Rotate(Vector3.forward, yaw); } }
        public Vector3 right { get { return Rotate(Vector3.right, yaw); } }
        public Matrix4x4 localToWorldMatrix { get { return new Matrix4x4 { Position = position, Yaw = yaw }; } }
        public bool IsChildOf(Transform other) { for (Transform t = this; t != null; t = t.parent) if (t == other) return true; return false; }
        private static Vector3 Rotate(Vector3 p, float angle) { float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle); return new Vector3(p.x*c + p.z*s, p.y, p.z*c - p.x*s); }
        public Vector3 TransformPoint(Vector3 p) { return position + TransformVector(p); }
        public Vector3 TransformVector(Vector3 p) { p=new Vector3(p.x*lossyScale.x,p.y*lossyScale.y,p.z*lossyScale.z); return Basis==null ? Rotate(p,yaw) : Basis[0]*p.x+Basis[1]*p.y+Basis[2]*p.z; }
        public Vector3 InverseTransformPoint(Vector3 p) { p = Rotate(p - position, -yaw); return new Vector3(p.x/lossyScale.x, p.y/lossyScale.y, p.z/lossyScale.z); }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x=a; y=b; z=c; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 one { get { return new Vector3(1,1,1); } }
        public static Vector3 up { get { return new Vector3(0,1,0); } }
        public static Vector3 down { get { return new Vector3(0,-1,0); } }
        public static Vector3 right { get { return new Vector3(1,0,0); } }
        public static Vector3 forward { get { return new Vector3(0,0,1); } }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
        public Vector3 normalized { get { return magnitude < .000001f ? zero : this/magnitude; } }
        public void Normalize() { this = normalized; }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a) { return a * -1; }
        public static float Angle(Vector3 a,Vector3 b) { return (float)Math.Acos(Math.Clamp(Dot(a.normalized,b.normalized),-1,1))*Mathf.Rad2Deg; }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x*b,a.y*b,a.z*b); }
        public static Vector3 operator /(Vector3 a, float b) { return a*(1/b); }
        public static float Dot(Vector3 a, Vector3 b) { return a.x*b.x+a.y*b.y+a.z*b.z; }
        public static float Distance(Vector3 a, Vector3 b) { return (a-b).magnitude; }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a+(b-a)*t; }
        public override bool Equals(object obj) { return obj is Vector3 v && x==v.x && y==v.y && z==v.z; }
        public override int GetHashCode() { return HashCode.Combine(x,y,z); }
    }
    public struct Vector2 { public float x,y; public Vector2(float a,float b) { x=a;y=b; } public float magnitude { get { return (float)Math.Sqrt(x*x+y*y); } } }
    public struct Quaternion { public float Yaw; public static Quaternion Inverse(Quaternion q) {return new Quaternion {Yaw=-q.Yaw};} public static Quaternion operator *(Quaternion a,Quaternion b) {return new Quaternion {Yaw=a.Yaw+b.Yaw};} public static Quaternion LookRotation(Vector3 d) { return new Quaternion { Yaw=(float)Math.Atan2(d.x,d.z) }; } public static Vector3 operator *(Quaternion q,Vector3 p) { float c=(float)Math.Cos(q.Yaw),s=(float)Math.Sin(q.Yaw);return new Vector3(p.x*c+p.z*s,p.y,p.z*c-p.x*s); } }
    public struct Matrix4x4
    {
        public Vector3 Position; public float Yaw;
        public static bool operator ==(Matrix4x4 a,Matrix4x4 b) { return a.Equals(b); }
        public static bool operator !=(Matrix4x4 a,Matrix4x4 b) { return !a.Equals(b); }
        public override bool Equals(object o) { return o is Matrix4x4 m && Position.Equals(m.Position) && Yaw==m.Yaw; }
        public override int GetHashCode() { return HashCode.Combine(Position,Yaw); }
    }
    public struct Bounds
    {
        public Vector3 center, extents;
        public Bounds(Vector3 c,Vector3 size) { center=c;extents=size*.5f; }
        public Vector3 size { get { return extents*2; } }
        public bool Contains(Vector3 p) { return p.x>=min.x && p.x<=max.x && p.y>=min.y && p.y<=max.y && p.z>=min.z && p.z<=max.z; }
        public void Encapsulate(Bounds b) { Vector3 low=new Vector3(Math.Min(min.x,b.min.x),Math.Min(min.y,b.min.y),Math.Min(min.z,b.min.z)), high=new Vector3(Math.Max(max.x,b.max.x),Math.Max(max.y,b.max.y),Math.Max(max.z,b.max.z));center=(low+high)*.5f;extents=(high-low)*.5f; }
        public Vector3 min { get { return center-extents; } }
        public Vector3 max { get { return center+extents; } }
        public Vector3 ClosestPoint(Vector3 p) { return new Vector3(Mathf.Clamp(p.x,min.x,max.x),Mathf.Clamp(p.y,min.y,max.y),Mathf.Clamp(p.z,min.z,max.z)); }
    }
    public class Collider : Component
    {
        public bool enabled = true, isTrigger;
        public Bounds testBounds;
        public virtual Bounds bounds { get { return testBounds; } }
        public Vector3 ClosestPointOnBounds(Vector3 p) { return bounds.ClosestPoint(p); }
    }
    public class BoxCollider : Collider { public Vector3 center, size = Vector3.one; public override Bounds bounds { get { return new Bounds(transform.position+center,size); } } }
    public class SphereCollider : Collider { public Vector3 center; public float radius; }
    public class CapsuleCollider : Collider { public Vector3 center = new Vector3(0,.5f,0); public float radius=.2f,height=1; public int direction=1; public override Bounds bounds { get { return new Bounds(transform.position+center,new Vector3(radius*2,height,radius*2)); } } }
    public class TerrainCollider : Collider { }
    public class Rigidbody : Component { public bool useGravity=true;public float drag;public int constraints; }
    public class MeshCollider : Collider { public Mesh sharedMesh; public bool convex; }
    public class Mesh : Object { public bool isReadable; public Vector3[] vertices = new Vector3[0]; public int[] triangles = new int[0]; public int vertexCount { get { return vertices.Length; } } }
    public struct RaycastHit { public Collider collider; public Vector3 point, normal; public float distance; }
    public struct LayerMask { public int value; public static implicit operator LayerMask(int i) { return new LayerMask { value=i }; } }
    public enum QueryTriggerInteraction { Collide, Ignore }
    public static class Physics
    {
        public static Vector3 gravity = new Vector3(0,-9.81f,0);
        public static Func<Vector3,float,Collider[]> Nearby;
        public static Func<Collider,Vector3,Quaternion,Collider,Vector3,Quaternion,bool> Penetration=(a,p,q,b,r,s)=>false;
        public static Vector3 PenetrationDirection = Vector3.up;
        public static float PenetrationDistance = .01f;
        public static bool ComputePenetration(Collider a,Vector3 p,Quaternion q,Collider b,Vector3 r,Quaternion s,out Vector3 direction,out float distance) { bool hit=Penetration(a,p,q,b,r,s);direction=PenetrationDirection;distance=PenetrationDistance;return hit; }
        public static Func<Vector3,float,Vector3,float,RaycastHit[]> Sphere = (p,r,d,length) => new RaycastHit[0];
        public static Func<Vector3,float,Vector3,float,RaycastHit[]> GroundSphere = (p,r,d,length) => new RaycastHit[0];
        public static int SphereCastNonAlloc(Vector3 p,float r,Vector3 d,RaycastHit[] output,float length,int mask,QueryTriggerInteraction q) { return Copy((mask==1 || mask==((1<<9)|(1<<28)) ? GroundSphere : Sphere)(p,r,d,length),output); }
        public static Func<Vector3,Vector3,float,int,RaycastHit[]> Ray;
        public static Func<Vector3,Vector3,float,Collider[]> Overlap;
        public static Func<Vector3,Vector3,float,Vector3,float,RaycastHit[]> Cast;
        private static int Copy<T>(T[] source,T[] output) { int n=Math.Min(source.Length,output.Length); Array.Copy(source,output,n); return n; }
        public static Func<int,int,bool> IgnoreLayers = (a,b)=>false;
        public static bool GetIgnoreLayerCollision(int a,int b) { return IgnoreLayers(a,b); }
        public static int OverlapBoxNonAlloc(Vector3 p,Vector3 half,Collider[] output,Quaternion rotation,int mask,QueryTriggerInteraction q) { return Copy(Nearby(p,half.magnitude),output); }
        public static Vector3 LastBoxCenter,LastBoxHalf;
        public static int BoxCastNonAlloc(Vector3 p,Vector3 half,Vector3 d,RaycastHit[] output,Quaternion rotation,float length,int mask,QueryTriggerInteraction q) { LastBoxCenter=p;LastBoxHalf=half;return Copy(Sphere(p,half.magnitude,d,length),output); }
        public static int OverlapSphereNonAlloc(Vector3 p,float r,Collider[] output,int mask,QueryTriggerInteraction q) { return Copy(Nearby(p,r),output); }
        public static int RaycastNonAlloc(Vector3 p,Vector3 d,RaycastHit[] output,float length,int mask,QueryTriggerInteraction q) { return Copy(Ray(p,d,length,mask),output); }
        public static int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float r,Collider[] output,int mask,QueryTriggerInteraction q) { return Copy(Overlap(a,b,r),output); }
        public static int CapsuleCastNonAlloc(Vector3 a,Vector3 b,float r,Vector3 d,RaycastHit[] output,float length,int mask,QueryTriggerInteraction q) { return Copy(Cast(a,b,r,d,length),output); }
    }
    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f, Deg2Rad = .017453292f, PI = (float)Math.PI;
        public static float Tan(float x) { return (float)Math.Tan(x); }
        public static float Sin(float x) { return (float)Math.Sin(x); }
        public static float Cos(float x) { return (float)Math.Cos(x); }
        public static float Abs(float x) { return Math.Abs(x); }
        public static float Min(float a,float b) { return Math.Min(a,b); }
        public static int Min(int a,int b) { return Math.Min(a,b); }
        public static float Max(float a,float b) { return Math.Max(a,b); }
        public static float Clamp(float x,float a,float b) { return Min(b,Max(a,x)); }
        public static float Clamp01(float x) { return Clamp(x,0,1); }
        public static float Lerp(float a,float b,float t) { return a+(b-a)*Clamp01(t); }
        public static int RoundToInt(float x) { return (int)Math.Round(x); }
        public static int FloorToInt(float x) { return (int)Math.Floor(x); }
        public static int CeilToInt(float x) { return (int)Math.Ceiling(x); }
        public static float Acos(float x) { return (float)Math.Acos(x); }
    }
    public static class Time { public static int frameCount; public static float realtimeSinceStartup, deltaTime=1f/60, unscaledDeltaTime=1f/60, fixedDeltaTime=.02f; }
}

#pragma warning disable CS0414 // Reflection fields must match the installed game.
public class GroundCast : UnityEngine.MonoBehaviour {
    private UnityEngine.LayerMask m_landscapeMask = (1 << 9) | (1 << 28);
    public UnityEngine.Vector3 Normal = UnityEngine.Vector3.up;
    public UnityEngine.Vector3 GetGroundPoint() { return transform.position; }
    public UnityEngine.Vector3 GetGroundNormal() { return Normal; }
    public float Distance;
    public float GetGroundDistance() { return Distance; }
}
public class PlayerControlsImpl_Default { }
public class PlayerControls : UnityEngine.MonoBehaviour
{
    private bool m_bGridSelection = true;
    private UnityEngine.LayerMask m_interactMask = ~0;
    private UnityEngine.LayerMask m_catchMask = ~0;
    public sealed class MovementData { public float StepHeightMax=.65f,RunSpeed=4,MaxSpeed=4,TurnSpeed=360; }
    public MovementData Movement = new MovementData();
    public float MovementScale = 1;
    public bool m_bRespawning;
    public bool GetDirectlyUnderPlayerControl() { return true; }
    public UnityEngine.Collider GroundCollider;
    public PlayerIDProvider PlayerIDProvider { get { return GetComponent<PlayerIDProvider>(); } }
    public WindAccumulator WindReceiver = new WindAccumulator();
    public SurfaceMovable SurfaceMovable = new SurfaceMovable();
    public RigidbodyMotion Motion = new RigidbodyMotion();
    public PlayerPhysicsSurface PhysicsSurface;
    public InteractionObjects CurrentInteractionObjects = new InteractionObjects();
    public object GetActiveControlsImpl() { return new PlayerControlsImpl_Default(); }
}
public class PlayerIDProvider : UnityEngine.Component { public int Id=1; public int GetID() { return Id; } }
public static class PlayerInputLookup
{
    public class Entry { public bool IsLocal() { return true; } }
    public class Config { public Entry GetInputConfigEntry(int id) { return new Entry(); } }
    public static Config GetInputConfig() { return new Config(); }
}
public class WindVolume : UnityEngine.MonoBehaviour { private UnityEngine.LayerMask m_windFilter = ~0; public UnityEngine.Vector3 Velocity; public UnityEngine.Vector3 GetVelocity() { return Velocity; } }
#pragma warning restore CS0414
public class WindAccumulator : UnityEngine.MonoBehaviour { public UnityEngine.Vector3 Velocity; public UnityEngine.Vector3 GetVelocity() { return Velocity; } }
public class SurfaceMovable : UnityEngine.MonoBehaviour
{
    private IMovingSurface m_prevSurface;
    public void SetPrevious(IMovingSurface value) { m_prevSurface=value; }
    public UnityEngine.Vector3 Velocity; public UnityEngine.Vector3 GetVelocity() { return Velocity; }
}
public class RigidbodyMotion { public UnityEngine.Vector3 Velocity; public UnityEngine.Vector3 GetVelocity() { return Velocity; } public UnityEngine.Vector3 GetVelocityXZ() { return new UnityEngine.Vector3(Velocity.x,0,Velocity.z); } }
public interface IMovingSurface { UnityEngine.Vector3 CalculateVelocityAtPoint(UnityEngine.Vector3 point, IMovingSurface previous); }
public class PlayerPhysicsSurfaceProperties { public bool UseOverrideGravityNormal; public float Slidiness,Slippiness,SpeedMultiplier=1; }
public class PlayerPhysicsSurface : UnityEngine.MonoBehaviour { public PlayerPhysicsSurfaceProperties Properties = new PlayerPhysicsSurfaceProperties(); }
public class Steppable : UnityEngine.MonoBehaviour { }
public class RespawnCollider : UnityEngine.MonoBehaviour { }
public class ServerFireHazard : UnityEngine.MonoBehaviour { }
public class ManualAnimation { public float m_CurveTime = 1; public UnityEngine.Vector3 m_TargetPositionValue; }
public class Teleportal : UnityEngine.MonoBehaviour
{
    public UnityEngine.GameObject m_exitPortal;
    public UnityEngine.Transform m_teleportPoint;
    public float m_teleportArc = 90, m_receiveDelay, m_cooldownTime;
}
public class ServerTeleportablePlayer : UnityEngine.MonoBehaviour
{
    public bool Travelling;
    public bool CanTeleport(ServerTeleportal p) { return enabled && !Travelling; }
    public bool IsTeleporting() { return Travelling; }
}
public class ClientTeleportablePlayer : UnityEngine.MonoBehaviour
{ public bool Travelling; public bool IsTeleporting() { return Travelling; } }
public class ServerTeleportal : UnityEngine.MonoBehaviour
{
    public bool Ready = true, Receiving;
    public bool CanTeleport(ServerTeleportablePlayer p) { return Ready; }
    public bool IsReceiving() { return Receiving; }
}
public class TeleportalPlayerSender : UnityEngine.MonoBehaviour { public ManualAnimation m_SenderAnimation = new ManualAnimation(); }
public class ServerTeleportalPlayerSender : UnityEngine.MonoBehaviour { }
public class TeleportalPlayerReceiver : UnityEngine.MonoBehaviour
{
    public UnityEngine.Transform m_attachPoint;
    public bool m_groundPlayer = true;
    public ManualAnimation m_ReceiverAnimation = new ManualAnimation();
}
public class ServerTeleportalPlayerReceiver : UnityEngine.MonoBehaviour
{ public bool Ready = true, Receiving; public bool IsReceiving() { return Receiving; } public bool CanTeleportTo(ServerTeleportablePlayer p) { return Ready; } }
public class WashingStation : UnityEngine.MonoBehaviour { }
public class RubbishBin : UnityEngine.MonoBehaviour { }
public class ServerWorkstation : UnityEngine.MonoBehaviour { }
public class PickupItemSpawner : UnityEngine.MonoBehaviour { public UnityEngine.GameObject m_itemPrefab; }
public class CookingStation : UnityEngine.MonoBehaviour { }
public class PlateReturnStation : UnityEngine.MonoBehaviour { }
public class PlateStackBase : UnityEngine.MonoBehaviour { }
public class PhysicalAttachment : UnityEngine.MonoBehaviour { public UnityEngine.Rigidbody m_container; public RigidbodyMotion m_motion; public RigidbodyParams m_rigidBodyParams = new RigidbodyParams(); public class RigidbodyParams { public float m_drag; public int m_constraints; public bool m_useGravity=true; } }
public class AttachStation : UnityEngine.MonoBehaviour { }
public interface ICarrier { UnityEngine.GameObject InspectCarriedItem(); }
public delegate void VoidGeneric<T,U>(T first,U second);
public interface IPickup { bool CanHandlePickup(ICarrier carrier); }
public interface IPlacement { bool CanHandlePlacement(ICarrier carrier,UnityEngine.Vector2 forward,PlacementContext context); }
public class PlacementContext { public enum Source { Player } public PlacementContext(Source s) { } }
public class ClientPlayerAttachmentCarrier : UnityEngine.MonoBehaviour, ICarrier { public UnityEngine.GameObject Item; public UnityEngine.GameObject InspectCarriedItem() { return Item; } }
public class ClientInteractable : UnityEngine.MonoBehaviour { public bool UsePlacementButton, Allowed=true; public int Users; public int InteractorCount() { return Users; } public bool CanInteract(UnityEngine.GameObject chef) { return Allowed; } }
public class InteractionObjects
{
    public UnityEngine.GameObject m_TheOriginalHandlePickup;
    public IPickup m_iHandlePickup;
    public IPlacement m_iHandlePlacement;
    public ClientInteractable m_interactable;
    public object m_gridLocation;
}
public static class PlayerControlsHelper
{
    public static IPickup GetControllingPickupHandler_Client(UnityEngine.GameObject go) { return go.GetComponent<IPickup>(); }
    public static IPlacement GetControllingPlacementHandler_Client(UnityEngine.GameObject go) { return go.GetComponent<IPlacement>(); }
    public static void TurnTowardsDirection(UnityEngine.GameObject go,UnityEngine.Vector3 direction,float speed,float dt) { go.transform.yaw=(float)Math.Atan2(direction.x,direction.z); }
}
public struct GridIndex { public int X,Y,Z; }
public interface IGridLocation { GridManager AccessGridManager { get; } GridIndex GridIndex { get; } }
public class GridManager : UnityEngine.MonoBehaviour
{
    public UnityEngine.GameObject Station;
    public GridIndex GetUnclampedGridLocationFromPos(UnityEngine.Vector3 p) { p=transform.InverseTransformPoint(p); return new GridIndex { X=UnityEngine.Mathf.RoundToInt(p.x),Y=UnityEngine.Mathf.RoundToInt(p.y),Z=UnityEngine.Mathf.RoundToInt(p.z) }; }
    public UnityEngine.Vector3 GetPosFromGridLocation(GridIndex at) { return transform.TransformPoint(new UnityEngine.Vector3(at.X,at.Y,at.Z)); }
    public UnityEngine.GameObject GetGridOccupant(GridIndex at) { return at.X==0 && at.Y==0 && at.Z==0 ? Station : null; }
}
namespace SousChef.Plugin { public static class Values { public static object Vector(UnityEngine.Vector3 p) { return new[] {p.x,p.y,p.z}; } } }

using System;
using UnityEngine;

namespace SousChef.Plugin.Navigation
{
    internal sealed partial class WalkPhysics
    {
        public bool DiscoveryGround(Collider c)
        {
            return c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy &&
                c.gameObject.layer == LayerMask.NameToLayer("Ground") &&
                (groundMask & (1 << c.gameObject.layer)) != 0 && !Own(c) && !Dangerous(c) &&
                c.bounds.size.x > .2f && c.bounds.size.z > .2f;
        }
        public void DiscoveryBounds(Collider c, out Vector3 low, out Vector3 high)
        {
            var box = c as BoxCollider;
            var mesh = c as MeshCollider;
            if (box != null) { low=box.center-box.size*.5f; high=box.center+box.size*.5f; }
            else if (mesh != null && mesh.sharedMesh != null) { low=mesh.sharedMesh.bounds.min; high=mesh.sharedMesh.bounds.max; }
            else
            {
                low=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
                high=-low;
                Bounds bounds=c.bounds;
                for(int x=0;x<2;x++) for(int y=0;y<2;y++) for(int z=0;z<2;z++)
                {
                    Vector3 p=c.transform.InverseTransformPoint(new Vector3(x==0?bounds.min.x:bounds.max.x,
                        y==0?bounds.min.y:bounds.max.y,z==0?bounds.min.z:bounds.max.z));
                    low=Vector3.Min(low,p); high=Vector3.Max(high,p);
                }
            }
        }
        public bool DiscoveryStand(Collider c, Vector3 localSeed, out Vector3 point, out Collider ground)
        {
            point=c.transform.TransformPoint(localSeed); ground=null;
            // Native raycasts find the actual surface of irregular meshes. The
            // capsule/footprint proof below rejects holes, scenery and ceilings.
            Vector3 origin=new Vector3(point.x,c.bounds.max.y+.1f,point.z);
            int count=Physics.RaycastNonAlloc(origin,Vector3.down,hits,c.bounds.size.y+.25f,groundMask,QueryTriggerInteraction.Ignore); Queries++;
            if (count==hits.Length) { Overflow=true; return false; }
            float height=Single.NegativeInfinity;
            for(int i=0;i<count;i++) if(hits[i].collider==c && hits[i].normal.y>.52992f) height=Mathf.Max(height,hits[i].point.y);
            if(float.IsNegativeInfinity(height)) return false;
            point.y=height; Vector3 supported;
            if(!StationaryStand(point,out supported,out ground) || ground!=c) return false;
            point=supported; return true;
        }
    }
}

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
            if (box != null) { low=box.center-box.size*.5f; high=box.center+box.size*.5f; }
            else { low=c.transform.InverseTransformPoint(c.bounds.min); high=c.transform.InverseTransformPoint(c.bounds.max); }
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

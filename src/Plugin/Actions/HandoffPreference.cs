using System;
using UnityEngine;

namespace SousChef.Plugin.Actions
{
    // A soft preference: safe, reachable delivery still wins when the ideal
    // ring is obstructed. Scores are seconds, so travel can outweigh spacing.
    internal static class HandoffPreference
    {
        public const float PreferredTiles = 3;
        public static float TileSize(PlayerControls player)
        {
            var location = player.CurrentInteractionObjects.m_gridLocation as IGridLocation;
            var grid = location == null ? null : location.AccessGridManager as QuadGridManager;
            if (grid == null) return 1.2f; // Native standard grid spacing.
            Vector3 size = ActionObservations.Read<Vector3>(grid, "m_size"), scale = grid.transform.lossyScale;
            return Math.Max(.1f, (float)Math.Sqrt(Math.Abs(size.x * scale.x * size.z * scale.z)));
        }
        public static float Distance(Vector3 a, Vector3 b)
        { Vector3 delta = a - b; delta.y = 0; return delta.magnitude; }
        public static float SpacingCost(float distance, float tileSize)
        { return Math.Abs(distance / tileSize - PreferredTiles) * .6f; }
        public static float Score(float seconds, float distance, float tileSize)
        { return seconds + SpacingCost(distance, tileSize); }
    }
}

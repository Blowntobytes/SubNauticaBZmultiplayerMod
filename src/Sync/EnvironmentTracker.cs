using UnityEngine;

namespace BZMultiplayer.Sync
{
    /// <summary>
    /// Works out which habitat (base or docked sub) a world position is inside, so a remote player's avatar can be lit
    /// by that habitat's sky the way the local player's body is. Bases are scanned rarely and cached.
    /// </summary>
    internal static class EnvironmentTracker
    {
        private static Base[] bases = new Base[0];
        private static float nextScan;

        /// <summary>The environment object to hand to SkyEnvironmentChanged, or null when outdoors.</summary>
        public static GameObject FindAt(Vector3 pos)
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 3f;
                bases = Object.FindObjectsOfType<Base>();
            }
            for (int i = 0; i < bases.Length; i++)
            {
                var b = bases[i];
                if (b == null || b.isGhost || b.cells == null) continue;
                try
                {
                    if (b.GetCell(b.WorldToGrid(pos)) == Base.CellType.Empty) continue;
                }
                catch { continue; }
                var sub = b.GetComponentInParent<SubRoot>();
                return sub != null ? sub.gameObject : b.gameObject;
            }
            return null;
        }
    }
}

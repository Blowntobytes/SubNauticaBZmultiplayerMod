using System.Collections.Generic;

namespace BZMultiplayer.Sync
{
    /// <summary>All remote players in the current session, keyed by SteamID64.</summary>
    public sealed class PlayerRegistry
    {
        private readonly Dictionary<ulong, RemotePlayer> byId = new Dictionary<ulong, RemotePlayer>();
        private readonly List<RemotePlayer> list = new List<RemotePlayer>();

        public IEnumerable<RemotePlayer> All { get { return list; } }
        public int Count { get { return list.Count; } }

        public RemotePlayer Get(ulong id)
        {
            RemotePlayer p;
            return byId.TryGetValue(id, out p) ? p : null;
        }

        public RemotePlayer GetOrAdd(ulong id, string name, bool isVr)
        {
            var p = Get(id);
            if (p != null)
            {
                if (!string.IsNullOrEmpty(name)) p.Name = name;
                p.IsVR = isVr;
                return p;
            }
            p = new RemotePlayer(id, string.IsNullOrEmpty(name) ? id.ToString() : name, isVr);
            byId[id] = p;
            list.Add(p);
            return p;
        }

        /// <summary>A player told us what they are holding; the avatar picks it up next frame.</summary>
        public void SetHeldItem(ulong id, int techType)
        {
            var p = Get(id);
            if (p != null) p.SetHeldItem((TechType)techType);
        }

        public void Remove(ulong id)
        {
            var p = Get(id);
            if (p == null) return;
            p.Destroy();
            byId.Remove(id);
            list.Remove(p);
        }

        public void Clear()
        {
            foreach (var p in list) p.Destroy();
            list.Clear();
            byId.Clear();
        }

        public void Update()
        {
            for (int i = 0; i < list.Count; i++) list[i].Update();
        }
    }
}

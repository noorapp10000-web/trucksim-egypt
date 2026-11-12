using System;
using System.Collections.Generic;
using UnityEngine;

namespace TruckSim
{
    /// <summary>One real Egyptian road, prepared offline by tools/roads.py from OpenStreetMap (geometry, lanes, bridges,
    /// median, jersey barriers, towns) and SRTM elevation (road profile + terrain cross-sections).
    /// Units: cm for x / zN / y / median, dm for terrain.</summary>
    [Serializable]
    public class RoadData
    {
        public string id, name, nameAr, origin, dest, originAr, destAr, region, source;
        public float step, lengthKm, laneWidth, shoulder;
        public int count;
        public int[] x, zN, y, lanes, median, bridge, maxspeed, curv;
        /// <summary>1 = concrete New-Jersey barrier on the centreline (optional: older files derive it from the median width).</summary>
        public int[] jersey;
        public int terrainEvery;
        public int[] terrainOffsets, terrainL, terrainR;
        public RoadTown[] towns;

        public bool HasJersey => jersey != null && jersey.Length == count;

        public static RoadData Load(string id)
        {
            var ta = Resources.Load<TextAsset>("Roads/" + id);
            if (ta == null) { Debug.LogError("Missing road data " + id); return null; }
            var d = JsonUtility.FromJson<RoadData>(ta.text);
            Resources.UnloadAsset(ta);
            if (d == null || d.count < 4 || d.x == null || d.x.Length != d.count) { Debug.LogError("Invalid road data " + id); return null; }
            return d;
        }
    }

    [Serializable]
    public class RoadTown { public string name, nameAr; public float km; }

    /// <summary>Garage road list. Read from the small Resources/Roads/catalog.json written by tools/roads.py,
    /// so opening the garage never parses the (large) road files themselves.</summary>
    public static class RoadCatalog
    {
        [Serializable]
        public class Entry
        {
            public string id, region, name, nameAr, origin, originAr, dest, destAr, lanes, elevation;
            public float km; public int bridges, towns;
        }
        [Serializable]
        public class Region { public string id, name, nameAr; public Entry[] routes; }
        [Serializable]
        class CatalogFile { public int version; public Region[] regions; }

        static Region[] regions;
        static Entry[] all;
        static string[] ids;

        static readonly string[] Legacy = { "R1_CairoAlex", "R2_CairoIsmailia", "R3_CairoSuez", "R4_CairoSokhna", "R5_Galala", "R6_CairoFayoum" };

        static void Ensure()
        {
            if (regions != null) return;
            var ta = Resources.Load<TextAsset>("Roads/catalog");
            CatalogFile cat = null;
            if (ta != null) { cat = JsonUtility.FromJson<CatalogFile>(ta.text); Resources.UnloadAsset(ta); }
            if (cat == null || cat.regions == null || cat.regions.Length == 0)
            {
                Debug.LogWarning("Road catalog missing, using the built-in list");
                var list = new List<Entry>();
                foreach (var id in Legacy) list.Add(new Entry { id = id, region = "cairo", name = id, km = 0, lanes = "?", elevation = "?" });
                cat = new CatalogFile { regions = new[] { new Region { id = "cairo", name = "Egypt", nameAr = "مصر", routes = list.ToArray() } } };
            }
            regions = cat.regions;
            var flat = new List<Entry>();
            foreach (var r in regions) if (r.routes != null) flat.AddRange(r.routes);
            all = flat.ToArray();
            ids = new string[all.Length];
            for (int i = 0; i < all.Length; i++) ids[i] = all[i].id;
        }

        public static Region[] Regions { get { Ensure(); return regions; } }
        public static Entry[] All { get { Ensure(); return all; } }
        /// <summary>Flat list in garage order (region by region).</summary>
        public static string[] Ids { get { Ensure(); return ids; } }
        public static int IndexOf(string id) { Ensure(); return Array.IndexOf(ids, id); }
        public static Entry Get(string id) { Ensure(); int i = IndexOf(id); return i >= 0 ? all[i] : null; }
        public static int RegionOf(string id) { Ensure(); for (int r = 0; r < regions.Length; r++) foreach (var e in regions[r].routes) if (e.id == id) return r; return 0; }
    }
}

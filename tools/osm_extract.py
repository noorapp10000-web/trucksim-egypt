"""Extracts the road-relevant parts of an OSM .pbf (Geofabrik Egypt extract) into a compact pickle:
highway ways (motorway..tertiary + links) with geometry and tags, jersey / concrete barriers and place nodes.
Used by roads.py as an offline fallback when the public Overpass API is busy.
Needs: pip install osmium (and libexpat/zlib/bz2/libstdc++ on the library path)."""
import pickle, sys, osmium

HW = {"motorway", "trunk", "primary", "secondary", "tertiary", "motorway_link", "trunk_link", "primary_link", "secondary_link"}
KEEP = ("highway", "name", "name:ar", "name:en", "ref", "lanes", "lanes:forward", "lanes:backward", "oneway", "bridge", "tunnel", "layer", "maxspeed", "maxspeed:hgv", "barrier", "junction")

class H(osmium.SimpleHandler):
    def __init__(self):
        super().__init__(); self.ways = []; self.barriers = []; self.places = []
    def node(self, n):
        t = n.tags
        if "place" in t and t["place"] in ("city", "town", "village") and "name" in t:
            self.places.append((n.location.lat, n.location.lon, t["place"], t.get("name"), t.get("name:ar", t.get("name")), t.get("name:en", t.get("name")), int(t.get("population", "0").replace(",", "") or 0) if t.get("population", "0").replace(",", "").isdigit() else 0))
        if t.get("barrier") == "jersey_barrier":
            self.barriers.append([(n.location.lat, n.location.lon)])
    def way(self, w):
        t = w.tags
        hw = t.get("highway")
        try:
            coords = [(nd.lat, nd.lon) for nd in w.nodes]
        except osmium.InvalidLocationError:
            return
        if hw in HW:
            self.ways.append((w.id, {k: t[k] for k in KEEP if k in t}, coords))
        elif t.get("barrier") in ("jersey_barrier", "concrete_barrier") or t.get("separator") == "jersey_barrier":
            self.barriers.append(coords)

if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    h = H(); h.apply_file(src, locations=True, idx="flex_mem")
    pickle.dump({"ways": h.ways, "barriers": h.barriers, "places": h.places}, open(dst, "wb"))
    print(f"ways={len(h.ways)} barriers={len(h.barriers)} places={len(h.places)}")

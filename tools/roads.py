#!/usr/bin/env python3
"""
TruckSimHighway road data builder (v3).

Builds Assets/Resources/Roads/<id>.json (format = RoadData.cs) and Resources/Roads/catalog.json for every
intercity road of the Egypt network defined in ROUTES below:

  * geometry   : OSRM route through the given anchors (anchors can be snapped onto a named OSM road)
  * attributes : OpenStreetMap ways matched to every 10 m sample (lanes, bridges, maxspeed, one-way / dual
                 carriageway, distance between carriageways = median width, jersey barriers, towns)
                 source = Overpass API (default) with automatic fall-back to a local Geofabrik extract
                 (tools/osm_extract.py) when the public Overpass servers are overloaded
  * elevation  : SRTM via AWS Terrain Tiles (Terrarium PNG, zoom 13), road profile + terrain cross sections

Usage:
  python3 tools/roads.py --list
  python3 tools/roads.py --check  UE1_CairoBeniSuefEast     # route + name match report only
  python3 tools/roads.py --build  all  --out TruckSimHighway/Assets/Resources/Roads
  python3 tools/roads.py --catalog --out TruckSimHighway/Assets/Resources/Roads
Options: --source auto|overpass|pbf   --pbf-cache /tmp/roadcache/egypt_roads.pkl
Units in the JSON: x / zN / y / median in cm, terrain in dm, km for towns (see RoadData.cs).
"""
import argparse, hashlib, io, json, math, os, pickle, re, sys, time, urllib.parse, urllib.request
import numpy as np
from scipy.spatial import cKDTree
from scipy.ndimage import gaussian_filter1d, median_filter, maximum_filter1d
from PIL import Image

R_EARTH = 6371008.8
CACHE = os.environ.get("ROADS_CACHE", "/tmp/roadcache")
UA = "TruckSimHighway-roads/3.0 (offline game data builder)"
OVERPASS = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter",
            "https://overpass.private.coffee/api/interpreter"]
OSRM = "https://router.project-osrm.org"
TERRARIUM = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"
DEM_Z = 13
STEP = 10.0
LANE_W = 3.65
SHOULDER = 2.5
TERRAIN_EVERY = 2
TERRAIN_OFFSETS = [12, 20, 30, 45, 65, 90, 125, 170, 230, 310, 420, 560, 750, 1000, 1350, 1800]
HW_RE = "^(motorway|trunk|primary|secondary|tertiary)(_link)?$"
DEFAULT_LANES = {"motorway": 3, "trunk": 2, "primary": 2, "secondary": 1, "tertiary": 1}

# ----------------------------------------------------------------------------------------------- network
REGIONS = [
    ("cairo", "Greater Cairo & Canal", "القاهرة الكبرى والقناة"),
    ("upper_east", "Upper Egypt - Eastern Desert Road", "الصعيد - الطريق الصحراوي الشرقي"),
    ("upper_west", "Upper Egypt - Western Desert Road", "الصعيد - الطريق الصحراوي الغربي"),
    ("red_sea", "Red Sea Coast", "ساحل البحر الأحمر"),
    ("coastal", "International Coastal Road", "الطريق الدولي الساحلي"),
    ("north_axes", "North Coast Axes", "محاور الساحل الشمالي"),
    ("delta", "Nile Delta", "الدلتا"),
    ("sinai", "Sinai", "سيناء"),
]

def A(on, lat=None, lon=None):  # noqa: E302
    """Anchor snapped onto the OSM road whose name matches `on`, nearest to the given latitude (or longitude)."""
    return {"on": on, "lat": lat, "lon": lon}

def P(lat, lon):
    return (lat, lon)

def RT(id, region, name, nameAr, origin, originAr, dest, destAr, via, expect=None):
    return dict(id=id, region=region, name=name, nameAr=nameAr, origin=origin, originAr=originAr, dest=dest, destAr=destAr, via=via, expect=expect)

EAST = r"Eastern Desert|Asyout Eastern|Cairo, Asyout Desert Road|الصحراوى الشرقى|الصحراوي الشرقي|اسيوط الصحراوي|اسيوط الشرقى"
WEST = r"Western Desert|Desert Western|الصحراوى الغربى|الصحراوي الغربي"
COAST = r"International Coastal|الساحلى الدولى|الدولى الساحلى|الدولي الساحلي"
DABAA = r"Dabaa Cairo Road|Al Dabaa Cairo|الضبعه القاهرة|الضبعة القاهرة|Dabaa Axis|محور الضبع"
REDSEA = r"Red Sea|البحر الأحمر|البحر الاحمر|Zafarana|Hurghada|Safaga|Quseir|Marsa Alam|Ghareb|Gharib|الغردقة|سفاجا|القصير|مرسى علم|غارب|الزعفران"

ROUTES = [
    # ---- Upper Egypt, Eastern Desert Road (east bank of the Nile)
    RT("UE1_CairoBeniSuefEast", "upper_east", "Cairo - Beni Suef (Eastern Desert Rd)", "القاهرة - بني سويف (الصحراوي الشرقي)", "Cairo", "القاهرة", "Beni Suef", "بني سويف",
       [A(EAST, lat=29.95), A(EAST, 29.07, 31.17)], EAST),
    RT("UE2_BeniSuefMinyaEast", "upper_east", "Beni Suef - Minya (Eastern Desert Rd)", "بني سويف - المنيا (الصحراوي الشرقي)", "Beni Suef", "بني سويف", "Minya", "المنيا",
       [A(EAST, 29.07, 31.17), A(r"Cairo, Asyout Eastern|القاهرة, اسيوط الشرقى", 28.1, 31.2)], EAST),
    RT("UE3_MinyaAssiutEast", "upper_east", "Minya - Assiut (Eastern Desert Rd)", "المنيا - أسيوط (الصحراوي الشرقي)", "Minya", "المنيا", "Assiut", "أسيوط",
       [A(r"Cairo, Asyout Eastern|القاهرة, اسيوط الشرقى", 28.1, 31.2), A(EAST, 27.18, 31.33)], EAST + r"|اسيوط الصحراوى الغربى"),
    RT("UE4_AssiutSohagEast", "upper_east", "Assiut - Sohag (Eastern Desert Rd)", "أسيوط - سوهاج (الصحراوي الشرقي)", "Assiut", "أسيوط", "Sohag", "سوهاج",
       [A(EAST, 27.18, 31.33), A(EAST, 26.85, 31.5)], EAST),
    RT("UE5_SohagQenaEast", "upper_east", "Sohag (Akhmim East) - Qena (Eastern Desert Rd)", "سوهاج (شرق أخميم) - قنا (الصحراوي الشرقي)", "Sohag", "سوهاج", "Qena", "قنا",
       [P(26.9622, 31.9388), P(26.8799, 32.2391), P(26.782, 32.5797), P(26.5976, 32.7623), P(26.302, 32.7754), P(26.17, 32.72)], EAST + r"|^-$"),
    RT("UE6_QenaLuxor", "upper_east", "Qena - Luxor (East Bank Rd)", "قنا - الأقصر (البر الشرقي)", "Qena", "قنا", "Luxor", "الأقصر",
       [P(26.16, 32.73), A(r"Qena, Aswan Road", lat=25.95), P(25.70, 32.66)], r"Qena, Aswan|قنا, اسوان"),
    RT("UE7_LuxorAswanEast", "upper_east", "Luxor - Aswan (Eastern Rd)", "الأقصر - أسوان (الطريق الشرقي)", "Luxor", "الأقصر", "Aswan", "أسوان",
       [P(25.68, 32.66), A(r"Qena, Aswan Road", lat=25.3), A(EAST, lat=24.9), A(EAST, lat=24.4), A(EAST, lat=24.12)], EAST + r"|Qena, Aswan|قنا, اسوان"),
    # ---- Upper Egypt, Western Desert Road (west of the Nile valley)
    RT("UW1_GizaBeniSuefWest", "upper_west", "Giza - Beni Suef (Western Desert Rd)", "الجيزة - بني سويف (الصحراوي الغربي)", "Giza", "الجيزة", "Beni Suef", "بني سويف",
       [A(WEST, lat=29.84), A(WEST, lat=29.5), A(WEST, lat=29.1)], WEST),
    RT("UW2_BeniSuefMinyaWest", "upper_west", "Beni Suef - Minya (Western Desert Rd)", "بني سويف - المنيا (الصحراوي الغربي)", "Beni Suef", "بني سويف", "Minya", "المنيا",
       [A(WEST, lat=29.1), A(WEST, lat=28.6), A(WEST, lat=28.1)], WEST),
    RT("UW3_MinyaAssiutWest", "upper_west", "Minya - Assiut (Western Desert Rd)", "المنيا - أسيوط (الصحراوي الغربي)", "Minya", "المنيا", "Assiut", "أسيوط",
       [A(WEST, lat=28.1), A(WEST, 27.6, 30.63), A(WEST, 27.26, 30.9), A(WEST, 27.15, 30.9)], WEST),
    RT("UW4_AssiutSohagWest", "upper_west", "Assiut - Sohag (Western Desert Rd)", "أسيوط - سوهاج (الصحراوي الغربي)", "Assiut", "أسيوط", "Sohag", "سوهاج",
       [A(WEST, lat=27.15), A(WEST, lat=26.85), A(WEST, lat=26.53)], WEST),
    RT("UW5_SohagQenaWest", "upper_west", "Sohag - Qena (Western Desert Rd)", "سوهاج - قنا (الصحراوي الغربي)", "Sohag", "سوهاج", "Qena", "قنا",
       [A(WEST, lat=26.53), A(WEST, lon=31.75), A(WEST, lon=32.3), A(WEST, lon=32.69)], WEST),
    RT("UW6_QenaLuxorWest", "upper_west", "Qena - Luxor (Western Desert Rd)", "قنا - الأقصر (الصحراوي الغربي)", "Qena", "قنا", "Luxor", "الأقصر",
       [A(r"Qena, Luxor Desert Western", lat=26.12), A(r"Qena, Luxor Desert Western", lat=25.76)], WEST),
    RT("UW7_LuxorAswanWest", "upper_west", "Luxor - Aswan (Western Rd)", "الأقصر - أسوان (الطريق الغربي)", "Luxor", "الأقصر", "Aswan", "أسوان",
       [A(r"Luxor, Aswan Western", lat=25.55), A(r"Luxor, Aswan Western", lat=24.9), A(r"Luxor, Aswan Western", lat=24.05)], r"Luxor, Aswan Western|الاقصر, اسوان الغربى"),
    # ---- Red Sea coast
    RT("RS1_SokhnaZafarana", "red_sea", "Ain Sokhna - Zafarana (Coast Rd)", "العين السخنة - الزعفرانة (الساحلي)", "Ain Sokhna", "العين السخنة", "Zafarana", "الزعفرانة",
       [P(29.60, 32.34), P(29.40, 32.57), P(29.11, 32.65)], REDSEA + r"|Sokhna|السخنه"),
    RT("RS2_ZafaranaRasGharib", "red_sea", "Zafarana - Ras Gharib", "الزعفرانة - رأس غارب", "Zafarana", "الزعفرانة", "Ras Gharib", "رأس غارب",
       [P(29.11, 32.65), P(28.70, 32.92), P(28.36, 33.08)], REDSEA),
    RT("RS3_RasGharibHurghada", "red_sea", "Ras Gharib - Hurghada", "رأس غارب - الغردقة", "Ras Gharib", "رأس غارب", "Hurghada", "الغردقة",
       [P(28.36, 33.08), P(27.80, 33.40), P(27.26, 33.78)], REDSEA),
    RT("RS4_HurghadaSafaga", "red_sea", "Hurghada - Safaga", "الغردقة - سفاجا", "Hurghada", "الغردقة", "Safaga", "سفاجا",
       [P(27.18, 33.80), P(26.75, 33.93)], REDSEA),
    RT("RS5_SafagaQuseir", "red_sea", "Safaga - El Quseir", "سفاجا - القصير", "Safaga", "سفاجا", "El Quseir", "القصير",
       [P(26.72, 33.94), P(26.10, 34.27)], REDSEA),
    RT("RS6_QuseirMarsaAlam", "red_sea", "El Quseir - Marsa Alam", "القصير - مرسى علم", "El Quseir", "القصير", "Marsa Alam", "مرسى علم",
       [P(26.10, 34.27), P(25.60, 34.57), P(25.07, 34.89)], REDSEA),
    # ---- International Coastal Road
    RT("CO1_PortSaidDamietta", "coastal", "Port Said - Damietta (Coastal Rd)", "بورسعيد - دمياط (الدولي الساحلي)", "Port Said", "بورسعيد", "Damietta", "دمياط",
       [P(31.24, 32.25), A(COAST, lon=32.0), P(31.43, 31.75)], COAST),
    RT("CO2_DamiettaNewMansoura", "coastal", "Damietta - New Mansoura (Coastal Rd)", "دمياط - المنصورة الجديدة (الدولي الساحلي)", "Damietta", "دمياط", "New Mansoura", "المنصورة الجديدة",
       [P(31.43, 31.75), A(COAST, 31.47, 31.55), A(COAST, 31.53, 31.38)], COAST),
    RT("CO3_NewMansouraBaltim", "coastal", "New Mansoura - Baltim (Coastal Rd)", "المنصورة الجديدة - بلطيم (الدولي الساحلي)", "New Mansoura", "المنصورة الجديدة", "Baltim", "بلطيم",
       [A(COAST, 31.53, 31.38), A(COAST, 31.57, 31.2), A(COAST, 31.585, 31.08)], COAST),
    RT("CO4_BaltimKafrElSheikhRosetta", "coastal", "Baltim - Kafr El Sheikh coast - Rosetta", "بلطيم - كفر الشيخ - رشيد (الدولي الساحلي)", "Baltim", "بلطيم", "Rosetta", "رشيد",
       [A(COAST, 31.585, 31.08), A(COAST, 31.52, 30.8), A(COAST, 31.43, 30.5)], COAST),
    RT("CO5_RosettaAlexandria", "coastal", "Rosetta - Alexandria (Coastal Rd)", "رشيد - الإسكندرية (الدولي الساحلي)", "Rosetta", "رشيد", "Alexandria", "الإسكندرية",
       [A(COAST, 31.43, 30.5), A(COAST, 31.27, 30.2), P(31.22, 29.98)], COAST + r"|Abu Qir|أبو قير|ابوقير"),
    RT("CO6_AlexandriaAlamein", "coastal", "Alexandria - El Alamein (Coastal Rd)", "الإسكندرية - العلمين (الدولي الساحلي)", "Alexandria", "الإسكندرية", "El Alamein", "العلمين",
       [P(31.10, 29.78), A(COAST, 30.9, 29.4), A(COAST, 30.82, 29.0)], COAST),
    RT("CO7_AlameinMatrouh", "coastal", "El Alamein - Marsa Matrouh (Coastal Rd)", "العلمين - مرسى مطروح (الدولي الساحلي)", "El Alamein", "العلمين", "Marsa Matrouh", "مرسى مطروح",
       [A(COAST, 30.82, 29.0), A(COAST, 31.04, 28.4), A(COAST, 31.11, 27.8), P(31.34, 27.25)], COAST),
    RT("CO8_MatrouhSalloum", "coastal", "Marsa Matrouh - Salloum (Coastal Rd)", "مرسى مطروح - السلوم (الدولي الساحلي)", "Marsa Matrouh", "مرسى مطروح", "Salloum", "السلوم",
       [P(31.33, 27.20), P(31.43, 26.30), P(31.55, 25.17)], COAST + r"|Salloum|السلوم|Barrani|براني"),
    # ---- North coast axes
    RT("AX1_DabaaAxis", "north_axes", "El Dabaa Axis (Cairo - El Dabaa)", "محور الضبعة (القاهرة - الضبعة)", "6th of October", "6 أكتوبر", "El Dabaa", "الضبعة",
       [A(r"Dabaa Axis|محور الضبع", lon=31.1), A(DABAA, lon=30.2), A(DABAA, lon=29.3), A(DABAA, lat=30.6), P(31.03, 28.43)],
       r"Dabaa|الضبع"),
    RT("AX2_WadiNatrunAlamein", "north_axes", "Wadi El Natrun - El Alamein Rd", "طريق وادي النطرون - العلمين", "Wadi El Natrun", "وادي النطرون", "El Alamein", "العلمين",
       [A(r"Wadi Al Natroun, Al Alamein", lon=30.1), A(r"Wadi Al Natroun, Al Alamein", lon=29.4), A(r"Wadi Al Natroun, Al Alamein", lon=28.95)], r"Natroun|النطرون"),
    # ---- Delta
    RT("DE1_CairoTantaAgri", "delta", "Cairo - Tanta (Agricultural Rd)", "القاهرة - طنطا (الزراعي)", "Cairo", "القاهرة", "Tanta", "طنطا",
       [A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.13), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.22), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.32), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.42), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.52), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.65), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.78)], r"Agricultural|الزراعى"),
    RT("DE2_TantaAlexAgri", "delta", "Tanta - Alexandria (Agricultural Rd)", "طنطا - الإسكندرية (الزراعي)", "Tanta", "طنطا", "Alexandria", "الإسكندرية",
       [A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=30.80), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=31.0), A(r"Alexandria Agricultural|الاسكندرية الزراعى", lat=31.18)], r"Agricultural|الزراعى"),
    RT("DE3_ShubraBenhaFree", "delta", "Shubra - Benha Free Road", "محور شبرا - بنها الحر", "Shubra El Kheima", "شبرا الخيمة", "Benha", "بنها",
       [A(r"Shobra - Banha|بنها الحر", lat=30.15), A(r"Shobra - Banha|بنها الحر", lat=30.3), A(r"Shobra - Banha|بنها الحر", lat=30.47)], r"Banha Al Hor|بنها الحر"),
    # ---- Sinai
    RT("SI1_AhmedHamdiAbuZenima", "sinai", "Ahmed Hamdi Tunnel - Ras Sudr - Abu Zenima", "نفق الشهيد أحمد حمدي - رأس سدر - أبو زنيمة", "Ahmed Hamdi Tunnel", "نفق أحمد حمدي", "Abu Zenima", "أبو زنيمة",
       [P(30.104, 32.535), P(29.95, 32.615), P(29.59, 32.71), P(29.05, 33.10)], r"Ras Sedr|Sharm|سدر|شرم|Tour|الطور|Tunnel|نفق"),
    RT("SI2_AbuZenimaElTor", "sinai", "Abu Zenima - El Tor", "أبو زنيمة - الطور", "Abu Zenima", "أبو زنيمة", "El Tor", "الطور",
       [P(29.05, 33.10), P(28.65, 33.30), P(28.25, 33.62)], r"Ras Sedr|Tour|الطور|سدر|Sharm|شرم"),
    RT("SI3_ElTorSharm", "sinai", "El Tor - Sharm El Sheikh", "الطور - شرم الشيخ", "El Tor", "الطور", "Sharm El Sheikh", "شرم الشيخ",
       [P(28.25, 33.62), P(27.95, 34.05), P(27.91, 34.32)], r"Tour|الطور|Sharm|شرم"),
    RT("SI4_TabaNuweiba", "sinai", "Taba - Nuweiba", "طابا - نويبع", "Taba", "طابا", "Nuweiba", "نويبع",
       [P(29.49, 34.89), P(29.25, 34.75), P(29.04, 34.66)], r"Taba|طابا|Newibaa|Nuweiba|نويبع"),
]
LEGACY = [  # roads shipped with v2 (built by the earlier tool, kept as they are)
    ("R1_CairoAlex", "cairo"), ("R2_CairoIsmailia", "cairo"), ("R3_CairoSuez", "cairo"),
    ("R4_CairoSokhna", "cairo"), ("R5_Galala", "red_sea"), ("R6_CairoFayoum", "cairo"),
]

# ----------------------------------------------------------------------------------------------- http
def http(url, data=None, tries=4, timeout=120, backoff=4):
    last = None
    for t in range(tries):
        try:
            req = urllib.request.Request(url, data=data, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except Exception as e:  # noqa
            last = e
            time.sleep(backoff * (2 ** t))
    raise RuntimeError(f"HTTP failed {url[:90]}: {last}")

def cached(name, fn):
    os.makedirs(CACHE, exist_ok=True)
    p = os.path.join(CACHE, name)
    if os.path.exists(p):
        return pickle.load(open(p, "rb"))
    v = fn()
    pickle.dump(v, open(p, "wb"))
    return v

def key(*a):
    return hashlib.sha1(json.dumps(a, sort_keys=True, default=str).encode()).hexdigest()[:16]

# ----------------------------------------------------------------------------------------------- OSM sources
_PBF = None
def pbf(path):
    global _PBF
    if _PBF is None:
        _PBF = pickle.load(open(path, "rb"))
        _PBF["wmid"] = np.array([[np.mean([p[0] for p in c]), np.mean([p[1] for p in c])] for _, _, c in _PBF["ways"]])
    return _PBF

def overpass(query):
    body = urllib.parse.urlencode({"data": query}).encode()
    last = None
    for url in OVERPASS:
        try:
            raw = http(url, body, tries=2, timeout=200, backoff=8)
            if raw.lstrip()[:1] != b"{":
                raise RuntimeError(raw[:80])
            return json.loads(raw)
        except Exception as e:  # noqa
            last = e
    raise RuntimeError(f"Overpass unavailable: {last}")

def resolve_anchor(a, args):
    if isinstance(a, tuple):
        return a
    pat = re.compile(a["on"], re.I)
    best, bd = None, 1e9
    def consider(tags, coords):
        nonlocal best, bd
        n = tags.get("name", "") + "|" + tags.get("name:en", "") + "|" + tags.get("name:ar", "")
        if not pat.search(n):
            return
        for la, lo in coords:
            if a["lat"] is not None and a["lon"] is not None: d = math.hypot(la - a["lat"], (lo - a["lon"]) * 0.9)
            else: d = abs(la - a["lat"]) if a["lat"] is not None else abs(lo - a["lon"])
            if d < bd:
                bd, best = d, (la, lo)
    if args.source != "overpass" and os.path.exists(args.pbf_cache):
        for _, t, c in pbf(args.pbf_cache)["ways"]:
            if t.get("highway", "").split("_")[0] in ("motorway", "trunk", "primary", "secondary"):
                consider(t, c)
    else:
        q = f'[out:json][timeout:180];way["highway"~"{HW_RE}"]["name"~"{a["on"]}",i](22,24.5,31.8,35.5);out geom;'
        for e in overpass(q)["elements"]:
            consider(e.get("tags", {}), [(g["lat"], g["lon"]) for g in e.get("geometry", [])])
    if best is None:
        raise RuntimeError(f"anchor not found: {a}")
    return best

def osm_corridor(route_ll, args):
    """Ways / barriers / places around the route polyline. Returns (ways, barriers, places, source)."""
    simp = route_ll[:: max(1, len(route_ll) // 400)] + [route_ll[-1]]
    if args.source in ("auto", "overpass"):
        try:
            poly = ",".join(f"{la:.5f},{lo:.5f}" for la, lo in simp)
            q = (f'[out:json][timeout:180];(way(around:150,{poly})["highway"~"{HW_RE}"];'
                 f'way(around:60,{poly})["barrier"~"jersey_barrier|concrete_barrier"];'
                 f'node(around:60,{poly})["barrier"="jersey_barrier"];'
                 f'node(around:6000,{poly})["place"~"^(city|town|village)$"];);out geom;')
            res = cached("ovp_" + key(q), lambda: overpass(q))
            ways, barriers, places = [], [], []
            for e in res["elements"]:
                t = e.get("tags", {})
                if e["type"] == "way":
                    g = [(p["lat"], p["lon"]) for p in e.get("geometry", [])]
                    if "highway" in t: ways.append((e["id"], t, g))
                    else: barriers.append(g)
                elif "place" in t:
                    pop = t.get("population", "0").replace(",", "")
                    places.append((e["lat"], e["lon"], t["place"], t.get("name"), t.get("name:ar", t.get("name")), t.get("name:en", t.get("name")), int(pop) if pop.isdigit() else 0))
                elif t.get("barrier") == "jersey_barrier":
                    barriers.append([(e["lat"], e["lon"])])
            return ways, barriers, places, "overpass"
        except Exception as e:  # noqa
            if args.source == "overpass":
                raise
            print(f"   overpass failed ({str(e)[:70]}) -> using local OSM extract")
    d = pbf(args.pbf_cache)
    rl = np.array(route_ll)
    tree = cKDTree(rl)
    lat_lo, lat_hi = rl[:, 0].min() - 0.08, rl[:, 0].max() + 0.08
    lon_lo, lon_hi = rl[:, 1].min() - 0.08, rl[:, 1].max() + 0.08
    m = d["wmid"]
    cand = np.where((m[:, 0] > lat_lo - 0.3) & (m[:, 0] < lat_hi + 0.3) & (m[:, 1] > lon_lo - 0.3) & (m[:, 1] < lon_hi + 0.3))[0]
    ways = []
    for i in cand:
        wid, t, c = d["ways"][i]
        dd, _ = tree.query(np.array(c), k=1)
        if dd.min() < 0.0016:  # ~150 m
            ways.append((wid, t, c))
    barriers = [b for b in d["barriers"] if tree.query(np.array(b), k=1)[0].min() < 0.0007]
    places = [p for p in d["places"] if tree.query([p[0], p[1]], k=1)[0] < 0.06]
    return ways, barriers, places, "osm-extract"

def osrm(via):
    coords = ";".join(f"{lo:.6f},{la:.6f}" for la, lo in via)
    # bearing hints (direction of travel, +-80 deg) stop OSRM from snapping a waypoint onto the opposite
    # carriageway of a dual highway, which would add a long U-turn detour
    brg = []
    for i in range(len(via)):
        a, b = via[max(0, i - 1)], via[min(len(via) - 1, i + 1)]
        if i == 0: a, b = via[0], via[1]
        if i == len(via) - 1: a, b = via[-2], via[-1]
        ang = math.degrees(math.atan2((b[1] - a[1]) * math.cos(math.radians(a[0])), b[0] - a[0])) % 360
        brg.append(f"{ang:.0f},80")
    url = f"{OSRM}/route/v1/driving/{coords}?overview=full&geometries=geojson&steps=true&continue_straight=true&bearings={';'.join(brg)}"
    res = cached("osrm_" + key(url), lambda: json.loads(http(url)))
    if res.get("code") != "Ok":
        raise RuntimeError(f"OSRM: {res.get('code')} {res.get('message')}")
    r = res["routes"][0]
    names = []
    for leg in r["legs"]:
        for s in leg["steps"]:
            names.append((s.get("name", ""), s.get("ref", ""), s["distance"]))
    return [(la, lo) for lo, la in r["geometry"]["coordinates"]], r["distance"], names

# ----------------------------------------------------------------------------------------------- DEM
class DEM:
    def __init__(self):
        self.tiles = {}
    def tile(self, tx, ty):
        k = (tx, ty)
        if k not in self.tiles:
            p = os.path.join(CACHE, "dem", f"{DEM_Z}_{tx}_{ty}.png")
            if not os.path.exists(p):
                os.makedirs(os.path.dirname(p), exist_ok=True)
                open(p, "wb").write(http(TERRARIUM.format(z=DEM_Z, x=tx, y=ty)))
            a = np.asarray(Image.open(p).convert("RGB")).astype(np.float32)
            self.tiles[k] = a[:, :, 0] * 256 + a[:, :, 1] + a[:, :, 2] / 256 - 32768
        return self.tiles[k]
    def sample(self, lat, lon):
        lat = np.asarray(lat, float); lon = np.asarray(lon, float)
        n = 2 ** DEM_Z
        fx = (lon + 180) / 360 * n * 256 - 0.5
        fy = (1 - np.log(np.tan(np.radians(lat)) + 1 / np.cos(np.radians(lat))) / math.pi) / 2 * n * 256 - 0.5
        x0 = np.floor(fx).astype(np.int64); y0 = np.floor(fy).astype(np.int64)
        wx = fx - x0; wy = fy - y0
        out = np.zeros(lat.shape)
        def px(X, Y):
            v = np.empty(X.shape)
            tx, ty = X // 256, Y // 256
            for t in set(zip(tx.ravel().tolist(), ty.ravel().tolist())):
                m = (tx == t[0]) & (ty == t[1])
                v[m] = self.tile(*t)[Y[m] % 256, X[m] % 256]
            return v
        out = (px(x0, y0) * (1 - wx) * (1 - wy) + px(x0 + 1, y0) * wx * (1 - wy) + px(x0, y0 + 1) * (1 - wx) * wy + px(x0 + 1, y0 + 1) * wx * wy)
        return out

# ----------------------------------------------------------------------------------------------- geometry helpers
class Proj:
    def __init__(self, lat0, lon0, latm):
        self.lat0, self.lon0 = lat0, lon0
        self.kx = math.radians(1) * R_EARTH * math.cos(math.radians(latm)); self.kz = math.radians(1) * R_EARTH
    def fwd(self, lat, lon):
        return (np.asarray(lon) - self.lon0) * self.kx, (np.asarray(lat) - self.lat0) * self.kz
    def inv(self, x, z):
        return self.lat0 + np.asarray(z) / self.kz, self.lon0 + np.asarray(x) / self.kx

def resample(x, z, step):
    d = np.hypot(np.diff(x), np.diff(z)); keep = np.concatenate([[True], d > 0.05])
    x, z = x[keep], z[keep]
    s = np.concatenate([[0], np.cumsum(np.hypot(np.diff(x), np.diff(z)))])
    n = int(s[-1] // step) + 1
    si = np.arange(n) * step
    return np.interp(si, s, x), np.interp(si, s, z)

def tangents(x, z):
    tx, tz = np.gradient(x), np.gradient(z)
    l = np.hypot(tx, tz) + 1e-9
    return tx / l, tz / l

def tagint(v, default=0):
    m = re.match(r"\s*(\d+)", str(v or ""))
    return int(m.group(1)) if m else default

def is_oneway(t):
    return t.get("oneway") in ("yes", "1", "true", "-1") or t.get("highway") == "motorway" or t.get("junction") == "roundabout"

def lanes_dir(t):
    hw = t.get("highway", "primary").replace("_link", "")
    if is_oneway(t):
        n = tagint(t.get("lanes"), 0) or DEFAULT_LANES.get(hw, 1)
    else:
        n = tagint(t.get("lanes:forward"), 0) or (tagint(t.get("lanes"), 0) // 2) or (2 if hw in ("motorway", "trunk") else 1)
    if t.get("highway", "").endswith("_link"):
        n = min(n, 2)
    return int(np.clip(n, 1, 6))

# ----------------------------------------------------------------------------------------------- build one route
def build_route(rt, args, check_only=False):
    t0 = time.time()
    via = [resolve_anchor(a, args) for a in rt["via"]]
    ll, dist, names = osrm(via)
    expect = re.compile(rt["expect"], re.I) if rt.get("expect") else None
    if expect:
        on = sum(d for n, r, d in names if expect.search(n or "") or expect.search(r or ""))
    top = {}
    for n, r, d in names: top[n or r or "-"] = top.get(n or r or "-", 0) + d
    topn = sorted(top.items(), key=lambda kv: -kv[1])[:4]
    print(f"== {rt['id']}: {dist/1000:.1f} km  via {['%.3f,%.3f' % v for v in via]}")
    print("   main roads: " + "; ".join(f"{k} {v/1000:.0f}km" for k, v in topn) + (f"   | named-match {on/max(1, dist)*100:.0f}%" if expect else ""))
    if check_only:
        return None
    lat = np.array([p[0] for p in ll]); lon = np.array([p[1] for p in ll])
    proj = Proj(lat[0], lon[0], float(lat.mean()))
    x, z = proj.fwd(lat, lon)
    x, z = resample(np.asarray(x), np.asarray(z), STEP)
    slat, slon = proj.inv(x, z)
    ways, barriers, places, source = osm_corridor(list(zip(slat.tolist(), slon.tolist())), args)
    print(f"   OSM ({source}): {len(ways)} ways, {len(barriers)} barriers, {len(places)} places")

    # ---- densified OSM ways (local metres) with heading
    pts, hd, wi = [], [], []
    for k, (_, t, c) in enumerate(ways):
        if len(c) < 2: continue
        wx, wz = proj.fwd(np.array([p[0] for p in c]), np.array([p[1] for p in c]))
        wx, wz = np.asarray(wx), np.asarray(wz)
        if t.get("oneway") == "-1": wx, wz = wx[::-1], wz[::-1]
        for a in range(len(wx) - 1):
            seg = math.hypot(wx[a + 1] - wx[a], wz[a + 1] - wz[a])
            if seg < 0.01: continue
            m = max(1, int(seg // 4))
            f = np.arange(m) / m
            pts.append(np.stack([wx[a] + (wx[a + 1] - wx[a]) * f, wz[a] + (wz[a + 1] - wz[a]) * f], 1))
            hd.append(np.repeat([[(wx[a + 1] - wx[a]) / seg, (wz[a + 1] - wz[a]) / seg]], m, 0))
            wi.append(np.full(m, k))
    pts = np.concatenate(pts); hd = np.concatenate(hd); wi = np.concatenate(wi)
    tree = cKDTree(pts)
    tx, tz = tangents(x, z)
    n = len(x)
    match = np.full(n, -1); oppd = np.full(n, np.nan)
    one = np.array([is_oneway(t) for _, t, _ in ways])
    for i in range(n):
        idx = tree.query_ball_point([x[i], z[i]], 25.0)
        best, bs = -1, -1e9
        for j in idx:
            dot = hd[j, 0] * tx[i] + hd[j, 1] * tz[i]
            if one[wi[j]] and dot < 0.8: continue
            if abs(dot) < 0.8: continue
            dd = math.hypot(pts[j, 0] - x[i], pts[j, 1] - z[i])
            sc = abs(dot) * 10 - dd
            if sc > bs: bs, best = sc, j
        if best >= 0:
            match[i] = wi[best]
            if one[wi[best]]:
                # opposite carriageway on the left (Egypt drives on the right)
                cands = tree.query_ball_point([x[i], z[i]], 160.0)
                bd = 1e9
                for j in cands:
                    if not one[wi[j]]: continue
                    dot = hd[j, 0] * tx[i] + hd[j, 1] * tz[i]
                    if dot > -0.8: continue
                    rx, rz = pts[j, 0] - x[i], pts[j, 1] - z[i]
                    lat_off = rx * tz[i] - rz * tx[i]   # + = right of travel
                    along = rx * tx[i] + rz * tz[i]
                    if lat_off < -2.0 and abs(along) < 12 and -lat_off < bd: bd = -lat_off
                if bd < 1e9: oppd[i] = bd
    matched = match >= 0
    print(f"   matched {matched.mean()*100:.0f}% of samples, dual carriageway found on {np.isfinite(oppd).mean()*100:.0f}%")
    # fill unmatched with the nearest matched sample
    if matched.any():
        good = np.where(matched)[0]
        near = good[np.clip(np.searchsorted(good, np.arange(n)), 0, len(good) - 1)]
        prev = good[np.clip(np.searchsorted(good, np.arange(n)) - 1, 0, len(good) - 1)]
        pick = np.where(np.abs(near - np.arange(n)) < np.abs(prev - np.arange(n)), near, prev)
        mfill = match[pick]
    else:
        mfill = np.zeros(n, int)
    tags = [ways[k][1] if k >= 0 else {} for k in mfill]
    lanes = np.array([lanes_dir(t) for t in tags], float)
    lanes = median_filter(lanes, size=41, mode="nearest").round().astype(int)
    bridge = np.array([1 if (t.get("bridge") not in (None, "no")) and matched[i] else 0 for i, t in enumerate(tags)])
    bridge = (median_filter(bridge, size=3, mode="nearest") > 0).astype(int)
    maxspeed = np.array([tagint(t.get("maxspeed:hgv") or t.get("maxspeed"), 0) for t in tags])
    maxspeed = median_filter(maxspeed, size=31, mode="nearest")
    oneway_s = np.array([is_oneway(t) for t in tags])
    W = lanes * LANE_W
    med = np.where(np.isfinite(oppd), oppd - 2 * 0.6 - W, np.nan)
    # one-way roads without a mapped opposite carriageway (motorway / trunk) -> narrow concrete median
    med = np.where(oneway_s & ~np.isfinite(med), 1.0, med)
    med = np.where(~oneway_s, 0.0, med)
    med = np.clip(np.nan_to_num(med, nan=1.0), 0.0, 120.0)
    med = median_filter(med, size=61, mode="nearest")
    med = gaussian_filter1d(med, 6, mode="nearest")
    med = np.where(med < 0.35, 0.0, np.maximum(med, 0.6))
    # ---- centreline = middle of the median (the OSRM line follows the right-hand carriageway)
    off = np.where(med > 0, med / 2 + 0.6 + W / 2, 0.0)
    off = gaussian_filter1d(off, 12, mode="nearest")
    cx, cz = x - tz * off, z + tx * off
    cx, cz = gaussian_filter1d(cx, 1.5, mode="nearest"), gaussian_filter1d(cz, 1.5, mode="nearest")
    # resample the centreline at exactly STEP and carry attributes over
    s_old = np.concatenate([[0], np.cumsum(np.hypot(np.diff(cx), np.diff(cz)))])
    m2 = int(s_old[-1] // STEP) + 1
    s_new = np.arange(m2) * STEP
    fi = np.interp(s_new, s_old, np.arange(n))
    nx, nz = np.interp(s_new, s_old, cx), np.interp(s_new, s_old, cz)
    ii = np.clip(np.round(fi).astype(int), 0, n - 1)
    lanes, bridge, maxspeed, med = lanes[ii], bridge[ii], maxspeed[ii], med[ii]
    x, z, n = nx, nz, m2
    tx, tz = tangents(x, z)
    slat, slon = proj.inv(x, z)
    # jersey barriers: OSM-mapped ones + every paved (narrow) median
    jersey = ((med > 0) & (med <= 8.0)).astype(int)
    if barriers:
        bp = np.concatenate([np.stack(proj.fwd(np.array([p[0] for p in b]), np.array([p[1] for p in b])), 1) for b in barriers])
        bt = cKDTree(bp)
        dd, _ = bt.query(np.stack([x, z], 1), k=1)
        jersey = np.where((dd < 15) & (med > 0), 1, jersey)
    # ---- elevation
    dem = DEM()
    yraw = dem.sample(slat, slon)
    y = yraw.copy()
    if bridge.any():  # bridge decks span: interpolate between the abutments
        bi = np.where(bridge > 0)[0]; ok = np.where(bridge == 0)[0]
        if len(ok) > 1: y[bi] = np.interp(bi, ok, yraw[ok])
    # coastal roads: the Terrarium tiles carry sea-floor depths, never let them pull a road below sea level
    y = np.maximum(y, -3.0)
    y = gaussian_filter1d(y, 6, mode="nearest")
    # terrain cross sections every TERRAIN_EVERY samples
    rows = np.arange(0, n, TERRAIN_EVERY)
    offs = np.array(TERRAIN_OFFSETS, float)
    rxv, rzv = tz[rows], -tx[rows]        # right vector
    TL, TR = [], []
    for side, out in ((-1, TL), (1, TR)):
        px = x[rows][:, None] + side * rxv[:, None] * offs[None, :]
        pz = z[rows][:, None] + side * rzv[:, None] * offs[None, :]
        la, lo = proj.inv(px, pz)
        out.append(dem.sample(la, lo))
    TL, TR = np.maximum(TL[0], -5.0), np.maximum(TR[0], -5.0)   # sea -> flat beach just below sea level
    # ---- curvature (1e-6 / m, + = right)
    h = np.unwrap(np.arctan2(tx, tz))
    curv = gaussian_filter1d(np.gradient(h) / STEP, 3) * 1e6
    # ---- towns along the road
    towns = []
    if places:
        tr2 = cKDTree(np.stack([x, z], 1))
        for la, lo, kind, nm, nar, nen, pop in places:
            px, pz = proj.fwd(la, lo)
            d, k = tr2.query([float(px), float(pz)], k=1)
            lim = {"city": 6000, "town": 3500, "village": 1200}[kind]
            if kind == "village" and pop < 15000 and d > 600: continue
            if d <= lim:
                towns.append({"name": nen or nm, "nameAr": nar or nm, "km": round(k * STEP / 1000, 2), "_r": {"city": 0, "town": 1, "village": 2}[kind], "_d": d})
        towns.sort(key=lambda t: (t["_r"], t["_d"]))
        sel = []
        for t in towns:
            if any(abs(t["km"] - s["km"]) < 4 or t["nameAr"] == s["nameAr"] for s in sel): continue
            sel.append(t)
            if len(sel) >= 14: break
        towns = sorted(sel, key=lambda t: t["km"])
        for t in towns: t.pop("_r"); t.pop("_d")
    if not towns or towns[-1]["km"] < n * STEP / 1000 - 3:
        towns.append({"name": rt["dest"], "nameAr": rt["destAr"], "km": round((n - 1) * STEP / 1000, 2)})
    if towns[0]["km"] > 3:
        towns.insert(0, {"name": rt["origin"], "nameAr": rt["originAr"], "km": 0.0})

    ci = lambda a: [int(round(v)) for v in a]
    out = {
        "id": rt["id"], "name": rt["name"], "nameAr": rt["nameAr"], "origin": rt["origin"], "dest": rt["dest"],
        "originAr": rt["originAr"], "destAr": rt["destAr"], "region": rt["region"], "source": f"OSRM + {source} + SRTM(Terrarium z{DEM_Z})",
        "step": STEP, "count": int(n), "lengthKm": round((n - 1) * STEP / 1000, 2), "laneWidth": LANE_W, "shoulder": SHOULDER,
        "lon0": float(proj.lon0), "originLonLat": [round(float(slon[0]), 6), round(float(slat[0]), 6)],
        "x": ci(x * 100), "zN": ci(z * 100), "y": ci(y * 100), "lanes": ci(lanes), "median": ci(med * 100),
        "bridge": ci(bridge), "jersey": ci(jersey), "maxspeed": ci(maxspeed),
        "terrainEvery": TERRAIN_EVERY, "terrainOffsets": TERRAIN_OFFSETS,
        "terrainL": ci(TL.ravel() * 10), "terrainR": ci(TR.ravel() * 10), "curv": ci(curv), "towns": towns,
    }
    nb = int(np.sum(np.diff(np.concatenate([[0], bridge])) == 1))
    print(f"   -> {out['lengthKm']} km, lanes {lanes.min()}-{lanes.max()}, elev {y.min():.0f}..{y.max():.0f} m, bridges {nb}, "
          f"jersey {jersey.mean()*100:.0f}%, divided {(med>0).mean()*100:.0f}%, towns {len(towns)}  ({time.time()-t0:.0f}s)")
    return out

# ----------------------------------------------------------------------------------------------- catalog
def summary(d, region):
    lanes = d["lanes"]; y = d["y"]
    br = int(np.sum(np.diff(np.concatenate([[0], np.array(d["bridge"])])) == 1))
    lmin, lmax = min(lanes), max(lanes)
    return {"id": d["id"], "region": region, "name": d["name"], "nameAr": d.get("nameAr", d["name"]),
            "origin": d.get("origin", ""), "originAr": d.get("originAr", d.get("origin", "")), "dest": d.get("dest", ""), "destAr": d.get("destAr", d.get("dest", "")),
            "km": d["lengthKm"], "lanes": f"{lmin}" if lmin == lmax else f"{lmin}-{lmax}",
            "elevation": f"{min(y)/100:.0f} - {max(y)/100:.0f} m", "bridges": br,
            "towns": len(d.get("towns", []))}

def write_catalog(outdir):
    regs = []
    order = {r[0]: i for i, r in enumerate(REGIONS)}
    entries = []
    for rid, reg in LEGACY:
        p = os.path.join(outdir, rid + ".json")
        if os.path.exists(p): entries.append(summary(json.load(open(p)), reg))
    for rt in ROUTES:
        p = os.path.join(outdir, rt["id"] + ".json")
        if os.path.exists(p): entries.append(summary(json.load(open(p)), rt["region"]))
    for rid, name, nar in REGIONS:
        rs = [e for e in entries if e["region"] == rid]
        if rs: regs.append({"id": rid, "name": name, "nameAr": nar, "routes": rs})
    cat = {"version": 3, "regions": regs}
    json.dump(cat, open(os.path.join(outdir, "catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"catalog: {len(regs)} regions, {len(entries)} roads, {sum(e['km'] for e in entries):.0f} km")

META = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""

def write_meta(path):
    mp = path + ".meta"
    if not os.path.exists(mp):
        open(mp, "w").write(META.format(guid=hashlib.md5(os.path.basename(path).encode()).hexdigest()))

# ----------------------------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--check", nargs="*")
    ap.add_argument("--build", nargs="*")
    ap.add_argument("--catalog", action="store_true")
    ap.add_argument("--out", default="TruckSimHighway/Assets/Resources/Roads")
    ap.add_argument("--source", default="auto", choices=["auto", "overpass", "pbf"])
    ap.add_argument("--pbf-cache", default=os.path.join(CACHE, "egypt_roads.pkl"))
    args = ap.parse_args()
    sel = lambda ids: ROUTES if (not ids or ids == ["all"]) else [r for r in ROUTES if any(r["id"].startswith(i) for i in ids)]
    if args.list:
        for r in ROUTES: print(r["id"], "|", r["region"], "|", r["name"])
    if args.check is not None:
        for r in sel(args.check):
            try: build_route(r, args, check_only=True)
            except Exception as e: print("!!", r["id"], e)
    if args.build is not None:
        os.makedirs(args.out, exist_ok=True)
        fails = []
        for r in sel(args.build):
            try:
                d = build_route(r, args)
                p = os.path.join(args.out, r["id"] + ".json")
                tmp = p + ".tmp"
                json.dump(d, open(tmp, "w", encoding="utf-8"), ensure_ascii=False, separators=(",", ":"))
                os.replace(tmp, p)
                write_meta(p)
            except Exception as e:
                import traceback; traceback.print_exc(); fails.append(r["id"])
        if fails: print("FAILED:", fails)
    if args.catalog or args.build is not None:
        write_catalog(args.out)
        write_meta(os.path.join(args.out, "catalog.json"))

if __name__ == "__main__":
    main()

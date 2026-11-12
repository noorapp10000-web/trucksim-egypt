# Changes in this version (v1.1)

## Trucks
- Freightliner Cascadia 2020 (T5) was removed from the game: the model has no cab interior
  (hollow shell, road visible through the floor). The file was NOT deleted; it is kept in
  `../RemovedModels_NoInterior/T5_Cascadia.glb`.
- Added Caterpillar CT680 (T5_CatCT680.glb) with a full interior (dashboard, gauges, steering
  wheel, seats). Source: Sketchfab model 67cba8df57094b34ab83d4c67680f926 "Caterpillar CT680"
  by geyges (Sketchfab Free Standard licence).
- Scania S730: model was rendered at half size and could not drive; sizing now re-measures until
  the real length matches.
- Mercedes Actros: opaque windscreen replaced by transparent glass (applies to every truck whose
  glass material was opaque).
- DAF XF95: driver camera moved forward out of the sleeper curtain into the driver seat.

## Build
- Android: IL2CPP, ARM64, min Android 7.0 (API 24), APK at Builds/Android/HighwayTruckSim.apk.
- Editor menu "Truck Sim/QA: Render truck shots" renders outside + cab views of every truck.

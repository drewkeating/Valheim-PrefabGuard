# PrefabGuard

BepInEx/Jotunn plugin for Valheim. Refuses clients whose mods "loaded" but failed to register their prefabs, before they can damage a world. See `package/README.md` for what it does and its settings.

## Build
Needs Valheim and a BepInEx install (a Gale/r2modman profile works) for reference assemblies, and a C# compiler (Visual Studio Build Tools Roslyn `csc.exe`).

```powershell
./build.ps1                 # builds dist/PrefabGuard.dll
./build.ps1 -Package        # also builds dist/Scooby_Drew902-PrefabGuard-<version>.zip (Thunderstore layout)
./build.ps1 -ValheimDir 'D:\Steam\steamapps\common\Valheim' -BepInExDir '<profile>\BepInEx'
```

## Layout
- `src/PrefabGuard.cs` - the whole plugin
- `package/` - manifest.json, README.md, CHANGELOG.md, icon.png for publishing
- `build.ps1` - build + package script

Plugin ID: `scooby_drew902.valheim.prefabguard`.

## License
GPL-3.0. See `LICENSE`.

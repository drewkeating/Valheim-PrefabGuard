# PrefabGuard

A safety check for modded Valheim servers.

## The problem
The usual mod-compatibility check (Jotunn's NetworkCompatibility) only compares which mods are installed and their versions. If a mod crashes inside its own startup, it still counts as installed, but its build pieces and items never get registered. That client can then join, take ownership of an area it cannot properly see, and the world "repairs" itself by breaking every piece it does not recognize (roofs fall, structures collapse), and the server saves it.

## What it does
- When a player connects, the server sends the names of every prefab it has loaded.
- The client waits until its own game has finished registering mods, then checks each name against its own ZNetScene.
- If more than `MaxMissing` prefabs are absent, the client leaves the world and shows a popup naming how many are missing and a sample, and tells the server, which logs it and disconnects.
- A client that never answers within `GraceSeconds` is kicked.
- Everyone must have the mod installed (Jotunn `EveryoneMustHaveMod`). Install it on the server and every client.

## Config (`scooby_drew902.valheim.prefabguard.cfg`)
- `Enforce` (true): set to false to only log. Useful to check for false positives.
- `GraceSeconds` (60): how long the server waits for a client's answer.
- `MaxMissing` (0): how many missing prefabs are tolerated.
- `IgnoreContains` (empty): comma-separated name fragments that are not checked.
- `[Debug] PretendMissing` (empty): testing only. Names this client pretends not to have. Leave empty or you will be refused.

## Notes
- It cannot detect problems that are not about missing prefabs.
- A fresh Valheim install can make some mods crash with "Steamworks is not initialized". Run plain Valheim once first (new character, enter a world), then launch with your mod manager.

## License
GPL-3.0. Source code is linked from this mod's website.

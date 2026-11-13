# PrefabGuard

A safety check for modded Valheim servers.

## The problem
The usual mod-compatibility check (Jotunn's NetworkCompatibility) only compares which mods are installed and their versions. If a mod crashes inside its own startup, it still counts as installed, but its build pieces and items never get registered. That client can then join, take ownership of an area it cannot properly see, and the world "repairs" itself by breaking every piece it does not recognize (roofs fall, structures collapse), and the server saves it.

## What it does
- When a player connects, the server sends the names of every prefab it has loaded.
- The client waits until its own game has finished registering mods, then checks each name against its own ZNetScene.
- If any prefab is absent (other than ones the server lists in `IgnorePrefabs`), the client leaves the world and shows a message box saying how many are missing, with a few examples and a pointer to a report file (`BepInEx/PrefabGuard/missing-prefabs.log`) that lists all of them, and tells the server, which logs it and disconnects.
- A client that never answers within `GraceSeconds` is kicked.
- Everyone must have the mod installed (Jotunn `EveryoneMustHaveMod`). Install it on the server and every client.

## Config (`scooby_drew902.valheim.prefabguard.cfg`)
- `Enforce` (true): set to false to only log. Useful to check for false positives. If a client has it off but the server has it on, the server tells the client why before it disconnects it, and the client shows its message box then.
- `GraceSeconds` (60): how long the server waits for a client's answer. Allowed range 10 to 120; anything outside is moved to the nearest limit.
- `IgnorePrefabs` (empty): server setting. Comma-separated prefab names that clients may lack, for example `rae_Ashwood_big_roof_top` (one roof piece from OdinArchitect) or `rae_Ashwood_big_roof_*` (every prefab starting with `rae_Ashwood_big_roof_`). Names must match exactly (capital letters count), and `*` stands for any text. An entry that is only `*` is ignored. Every other missing prefab gets the client refused.
- `MessageBoxWidthFactor` (1.75): client setting. How much wider to make the disconnect message box (1 = the game's normal width). 0 turns it off, and values from 0 to 1 all give the game's normal width.
- `MessageBoxTextSizeFactor` (0.9): client setting. Text size in the disconnect message box, relative to the game's own message box text (1 = the game's size). The box grows taller to fit the text, but never taller than the screen (the text is made smaller if it would be). Range 0 to 2; a value above 0 but below 0.5 is treated as 0.5. 0 turns this off: the box keeps the game's fixed height and the game shrinks long text to fit. With `MessageBoxWidthFactor` = 1 as well, you get the game's normal message box size.
- `[Debug] PretendMissingPrefabs` (empty): testing only. Names this client pretends not to have, so you can test the mod without a real mismatch. Leave empty or you will be refused.
- `[Debug] ForceVanillaMessageBox` (false): testing only. Shows the game's own plain message box, the fallback used if PrefabGuard can't build its own.

## Notes
- It cannot detect problems that are not about missing prefabs.
- A fresh Valheim install can make some mods crash with "Steamworks is not initialized". Run plain Valheim once first (new character, enter a world), then launch with your mod manager.

## License
GPL-3.0. Source code is linked from this mod's website.

# Changelog

## 1.1.1
- "Open report" now opens the report file with the game's own link opener, and PrefabGuard no longer starts any program itself (1.1.0 ran `xdg-open` and `open` on Linux and macOS and used the Windows shell directly). On Windows it passes the plain file path (tested); on Linux and macOS it passes a `file://` address. Not tested on Linux or macOS, and the game's link opener isn't documented for local files there; if the button does nothing, the message box and the report file still work. Nothing else changed, and 1.1.1 works with 1.1.0 and the other way round (Jotunn compares only the major and minor numbers).

## 1.1.0

Everyone (every client and the server) must update to 1.1.0. The minor version changed on purpose: Jotunn refuses to connect a 1.0.x client to a 1.1.x server, and the other way round, so nobody is left on a blank "Failed to connect" screen.

### Message box
- Rebuilt as a copy of the game's own message box, so the game's shared one is never changed. It has a title, a "What Happened" section, a "What to do" section, a Details line, and two buttons: Open report and OK.
- The message tells a first world load (a mod crashed during startup) from a second or later one in the same game session (some mods only register their objects the first time a world loads, so restart the game and join again).
- The list of example prefab names is at most 50 characters.
- The mouse highlights only the button under it. Arrow keys, Tab and the controller d-pad or stick move the highlight, Enter, Space and A click it, and Escape and B always click OK. Nothing is highlighted when it opens.
- The text keeps the game's own size instead of shrinking, and the box grows taller to fit. It never gets taller or wider than 90% of the screen; if it would, the text is made smaller until it fits.
- If the copy can't be built, the game's plain one-button message box is used instead.

### Report file
- `BepInEx/PrefabGuard/missing-prefabs.log` lists every missing prefab and every loaded plugin. It is replaced the first time a join is refused in each game session, and later refusals in that session are added to it.
- "Open report" opens it in your default program (the Windows shell on Windows, `xdg-open` on Linux, `open` on macOS). Not tested on Linux or macOS.

### Server
- When a server refuses a client whose own `Enforce` is off, the server now tells the client why before it disconnects it (after about a second and a half), so the client shows the same message box and report.
- The server's log shows "world load #N this game session" when it applies.

### Settings
- Removed `MaxMissing`. Any missing prefab now gets the client refused, except ones on the server's `IgnorePrefabs` list.
- `IgnoreContains` is now `IgnorePrefabs`. It matches exact prefab names (capital letters count), and `*` stands for any text, for example `rae_Ashwood_big_roof_*`. An old `IgnoreContains` line is ignored.
- `GraceSeconds` has an allowed range of 10 to 120 seconds, and the server also guards against odd values, so a setting of 0 or less can no longer disconnect every client before it can answer.
- New client settings `MessageBoxWidthFactor` (default 1.75, 0 = off, range 0 to 3) and `MessageBoxTextSizeFactor` (default 0.9, 1 = the game's own text size, range 0 to 2, a value above 0 but below 0.5 counts as 0.5, 0 = off: the game's fixed-height box and shrink-to-fit text).
- The config is re-read each time PrefabGuard checks the server's list or shows a message box, so edits apply to the next join without restarting the game. Re-reading doesn't rewrite your config file.
- Testing only, under [Debug]: `PretendMissing` is now `PretendMissingPrefabs`, and the new `ForceVanillaMessageBox` shows the game's plain fallback box.

### Unchanged
- The check itself: a join is still refused when the server has prefabs the client hasn't loaded.

## 1.0.0
- Initial release.

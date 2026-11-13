using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using UnityEngine;

// PrefabGuard: stops a client whose mods "loaded" but failed to register their pieces from joining.
// Jotunn's NetworkCompatibility only compares installed mod names/versions. A mod that crashes inside
// its own Awake still counts as installed, so the server lets the client in and the client then breaks
// every piece it can't see. This plugin makes the server send the list of prefab names it has, and the
// client checks each one against its own ZNetScene before the world can be simulated.
namespace PrefabGuard
{
    [BepInPlugin(Id, "PrefabGuard", "1.1.0")]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    public class PrefabGuardPlugin : BaseUnityPlugin
    {
        public const string Id = "scooby_drew902.valheim.prefabguard";
        const string RpcNames = "PrefabGuard_Names";
        const string RpcAck = "PrefabGuard_Ack";
        const string RpcRefused = "PrefabGuard_Refused";

        internal static PrefabGuardPlugin Instance;
        internal static ConfigEntry<bool> Enforce;
        internal static ConfigEntry<float> GraceSeconds;
        internal static ConfigEntry<string> IgnorePrefabs;
        internal static ConfigEntry<string> DebugPretendMissingPrefabs;
        internal static ConfigEntry<bool> DebugForceVanillaMessageBox;
        internal static ConfigEntry<float> MessageBoxWidthFactor;
        internal static ConfigEntry<float> MessageBoxTextSizeFactor;

        // Server: peer uid -> time the server sent the list, until the client answers.
        static readonly Dictionary<long, float> Waiting = new Dictionary<long, float>();
        // Client: names received from the server, waiting for ZNetScene to exist.
        static List<string> pendingNames;
        static ZRpc pendingRpc;
        static float sceneSeenAt = -1f;
        static string popupMessage;
        const string PopupTitle = "PrefabGuard:\nDetected missing objects";
        // The popup text, in parts: what happened, what to do, and the pointer to the report file (null if none).
        static string popupHappened, popupToDo, popupDetails;
        // Client: how many times a world (ZNetScene) has been built since the game started.
        internal static int scenesBuilt;
        // Report file: replaced the first time a join is refused in each game session, then added to.
        static bool reportStarted;
        static DateTime sessionStart;
        static string reportPath;
        const string OpenReportLabel = "Open report";
        const string CloseLabel = "OK";

        void Awake()
        {
            Instance = this;
            sessionStart = DateTime.Now;
            Enforce = Config.Bind("General", "Enforce", true,
                "Server and client. Whether a client that lacks prefabs is actually stopped.\n" +
                "true: the client leaves the world, and the server disconnects it, before it can touch the world.\n" +
                "false: PrefabGuard only writes the problem to the log and lets the client stay. Use this to check for false alarms.\n" +
                "If a client has this off but the server has it on, the server tells the client why before it disconnects it, and the client shows the message box then (needs PrefabGuard 1.1.0 on both).\n" +
                "Leave this true for normal play.");
            GraceSeconds = Config.Bind("General", "GraceSeconds", 60f,
                new ConfigDescription("Server only. After the server sends its list of prefab names, how many seconds the client has to send back its answer.\n" +
                    "A client that stays silent for longer is disconnected (if Enforce is true).\n" +
                    "Raise this if players on slow computers are disconnected while the game is still loading.\n" +
                    "Values outside the allowed range are moved to the nearest limit, so a client can never be disconnected before it has had time to answer.",
                    new AcceptableValueRange<float>(MinGraceSeconds, MaxGraceSeconds)));
            IgnorePrefabs = Config.Bind("General", "IgnorePrefabs", "",
                "Server only. Prefabs that clients are allowed to lack. Every other missing prefab gets the client refused.\n" +
                "Separate names with commas. A name must match exactly, including capital letters.\n" +
                "Example: rae_Ashwood_big_roof_top ignores that one prefab (a roof piece from OdinArchitect).\n" +
                "A * stands for any text. Example: rae_Ashwood_big_roof_* ignores every prefab that starts with rae_Ashwood_big_roof_, such as rae_Ashwood_big_roof_top and rae_Ashwood_big_roof_icorner_46.\n" +
                "An entry that is only * is skipped, because it would switch the check off.\n" +
                "Leave this empty unless you have a reason.");
            MessageBoxWidthFactor = Config.Bind("General", "MessageBoxWidthFactor", 1.75f,
                new ConfigDescription("Client only. How much wider to make PrefabGuard's message box than the game's normal message box.\n" +
                    "1 = normal width, 1.75 = three quarters wider, 2 = twice as wide. Only PrefabGuard's message box is changed. The box is never wider than the screen allows.\n" +
                    "0 = off: the game's normal width. A value above 0 but below 1 is treated as 1, which is also the game's normal width.",
                    new AcceptableValueRange<float>(0f, 3f)));
            MessageBoxTextSizeFactor = Config.Bind("General", "MessageBoxTextSizeFactor", 0.9f,
                new ConfigDescription("Client only. How large the text in PrefabGuard's message box is, compared with the game's normal message box text.\n" +
                    "The game's normal message box shrinks text size until it fits a fixed-height box, which makes long messages small. This setting allows PrefabGuard to keep the text at the game's own size and makes the box taller to fit.\n" +
                    "1 = the game's normal text size, 0.8 = a fifth smaller, 1.3 = about a third larger, 2 = twice as large. Larger text makes the box taller; if it gets too tall, make the box wider with MessageBoxWidthFactor. The box never gets taller than the screen: if it would, the text is made smaller until it fits.\n" +
                    "A value above 0 but below 0.5 is treated as 0.5, because tiny text isn't useful. Exactly 0 is different, see below.\n" +
                    "0 = off: the box keeps the game's fixed height and the game shrinks long text to fit it, as before. Together with MessageBoxWidthFactor = 1, you get the game's normal message box size.",
                    new AcceptableValueRange<float>(0f, 2f)));
            DebugPretendMissingPrefabs = Config.Bind("Debug", "PretendMissingPrefabs", "",
                "Testing only, leave empty. Simulates a mismatch between the prefabs this client has loaded and the ones the server has.\n" +
                "A prefab is the game's template for something it can create in the world, such as a building piece, an item or a creature. A client needs every prefab the server has.\n" +
                "List exact prefab names, separated by commas. This client will claim not to have them, so you can test this mod's functions without the need for an actual mismatch.\n" +
                "While anything is listed here, servers that run PrefabGuard will refuse this client.");
            DebugForceVanillaMessageBox = Config.Bind("Debug", "ForceVanillaMessageBox", false,
                "Testing only, leave false. true: PrefabGuard shows the game's own plain, one-button message box instead of its own.\n" +
                "That plain box is what PrefabGuard falls back to if it can't build its own, so this lets you see it. It has no Open report button.");
            new Harmony(Id).PatchAll(typeof(PrefabGuardPlugin).Assembly);
            Logger.LogInfo("PrefabGuard loaded. Enforce=" + Enforce.Value);
        }

        // A prefab is ignored if it equals an IgnorePrefabs entry exactly, or matches an entry that contains * (any text).
        // An entry that is only * would switch the check off, so it is skipped.
        static bool Ignored(string name)
        {
            foreach (var raw in IgnorePrefabs.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry.Length == 0 || entry.Trim('*').Length == 0) continue;
                if (entry.IndexOf('*') < 0) { if (name == entry) return true; }
                else if (WildcardMatch(entry, name)) return true;
            }
            return false;
        }

        static bool WildcardMatch(string pattern, string text)
        {
            var parts = pattern.Split('*');
            int pos = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0) continue;
                if (i == 0)
                {
                    if (!text.StartsWith(part, StringComparison.Ordinal)) return false;
                    pos = part.Length;
                }
                else if (i == parts.Length - 1)
                    return text.Length - part.Length >= pos && text.EndsWith(part, StringComparison.Ordinal);
                else
                {
                    int idx = text.IndexOf(part, pos, StringComparison.Ordinal);
                    if (idx < 0) return false;
                    pos = idx + part.Length;
                }
            }
            return true;
        }

        void Update()
        {
            var znet = ZNet.instance;
            if (znet == null) { ShowPopup(); TickPopup(); return; }

            if (znet.IsServer()) ServerTick(znet);
            else ClientTick();
            ShowPopup();
            TickPopup();
        }

        // ---------------- server ----------------

        const float MinGraceSeconds = 10f;
        const float MaxGraceSeconds = 120f;

        // The setting is range-checked by BepInEx; this is a second guard so an odd value (0, negative, not a number)
        // can never make the server disconnect clients before they have had time to answer.
        static float Grace()
        {
            float g = GraceSeconds.Value;
            if (float.IsNaN(g) || g < MinGraceSeconds) return MinGraceSeconds;
            return g > MaxGraceSeconds ? MaxGraceSeconds : g;
        }

        // Server: clients that have been told they are refused, and when to disconnect each of them.
        static readonly Dictionary<long, float> Refusing = new Dictionary<long, float>();
        const float RefuseDelaySeconds = 1.5f;

        static void ServerTick(ZNet znet)
        {
            foreach (var uid in Refusing.Keys.ToList())
            {
                var refused = znet.GetPeer(uid);
                if (refused == null) { Refusing.Remove(uid); continue; }
                if (Time.realtimeSinceStartup < Refusing[uid]) continue;
                Refusing.Remove(uid);
                znet.Disconnect(refused);
            }
            if (Waiting.Count == 0) return;
            foreach (var uid in Waiting.Keys.ToList())
            {
                var peer = znet.GetPeer(uid);
                if (peer == null) { Waiting.Remove(uid); continue; }
                if (Time.realtimeSinceStartup - Waiting[uid] < Grace()) continue;
                Waiting.Remove(uid);
                Instance.Logger.LogWarning("[PrefabGuard] " + peer.m_playerName + " (" + uid + ") never answered the prefab check. " +
                    (Enforce.Value ? "Kicking." : "Enforce is off, allowing."));
                if (Enforce.Value) znet.Disconnect(peer);
            }
        }

        internal static void SendNames(ZNet znet, ZNetPeer peer)
        {
            if (ZNetScene.instance == null || peer == null) return;
            var names = ZNetScene.instance.GetPrefabNames().Where(n => !Ignored(n)).ToList();
            var pkg = new ZPackage();
            pkg.Write(names.Count);
            foreach (var n in names) pkg.Write(n);
            Waiting[peer.m_uid] = Time.realtimeSinceStartup;
            peer.m_rpc.Invoke(RpcNames, new object[] { pkg });
            Instance.Logger.LogInfo("[PrefabGuard] Sent " + names.Count + " prefab names to " + peer.m_playerName);
        }

        internal static void OnAck(ZRpc rpc, int missing, string sample)
        {
            var znet = ZNet.instance;
            var peer = znet == null ? null : PeerOf(znet, rpc);
            if (peer == null) return;
            Waiting.Remove(peer.m_uid);
            if (missing <= 0)
            {
                Instance.Logger.LogInfo("[PrefabGuard] " + peer.m_playerName + " passed (" + missing + " missing).");
                return;
            }
            Instance.Logger.LogWarning("[PrefabGuard] " + peer.m_playerName + " is missing " + missing + " prefabs, e.g. " + sample +
                (Enforce.Value ? ". Disconnecting." : ". Enforce is off, allowing."));
            if (!Enforce.Value) return;
            // Tell the client why first (a client with Enforce off would otherwise just see a blank "Failed to connect"), then
            // disconnect after a short wait so the message reaches it before the connection closes.
            try { peer.m_rpc.Invoke(RpcRefused, new object[] { missing }); } catch { }
            Refusing[peer.m_uid] = Time.realtimeSinceStartup + RefuseDelaySeconds;
        }

        // ---------------- client ----------------

        internal static void OnNames(ZRpc rpc, ZPackage pkg)
        {
            int count = pkg.ReadInt();
            var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(pkg.ReadString());
            pendingNames = list;
            pendingRpc = rpc;
            sceneSeenAt = -1f;
            Instance.Logger.LogInfo("[PrefabGuard] Received " + count + " prefab names from the server.");
        }

        static void ClientTick()
        {
            if (pendingNames == null) return;
            var scene = ZNetScene.instance;
            if (scene == null || scene.GetPrefabNames().Count == 0) { sceneSeenAt = -1f; return; }
            // Give every mod's Awake patch a couple of frames to finish registering into the scene.
            if (sceneSeenAt < 0f) { sceneSeenAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - sceneSeenAt < 0.5f) return;

            ReloadConfig();
            var names = pendingNames; var rpc = pendingRpc;
            pendingNames = null; pendingRpc = null;
            lastMissing = null;
            var fake = DebugPretendMissingPrefabs.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var missing = names.Where(n => scene.GetPrefab(n) == null || fake.Contains(n)).ToList();
            string sample = string.Join(", ", missing.Take(8).ToArray());
            // A second (or later) world load in the same game session can lose prefabs from mods that register
            // them only once. Say so in the logs on both sides (the server sees this in the ack's sample text).
            bool repeatLoad = scenesBuilt > 1 && missing.Count > 0;
            if (repeatLoad) sample += " [world load #" + scenesBuilt + " this game session]";
            Instance.Logger.LogInfo("[PrefabGuard] Checked " + names.Count + " prefabs, " + missing.Count + " missing" +
                (repeatLoad ? " (world load #" + scenesBuilt + " this game session)." : "."));
            try { rpc.Invoke(RpcAck, new object[] { missing.Count, sample }); } catch { }

            if (missing.Count == 0) return;
            Instance.Logger.LogError("[PrefabGuard] " + missing.Count + " prefabs the server has are missing here: " + string.Join(", ", missing.Take(40).ToArray()));
            if (!Enforce.Value)
            {
                // Stay connected. But if the server (which has its own Enforce) refuses this client anyway, it sends a reason,
                // and these saved results let the client show the same message box it would have shown on its own.
                lastMissing = missing; lastServerCount = names.Count; lastRepeat = repeatLoad;
                Instance.Logger.LogWarning("[PrefabGuard] Enforce is off, staying connected. If the server refuses this client anyway, the reason will be shown.");
                return;
            }
            ShowRefusal(names.Count, missing, repeatLoad);
        }

        // Saved results of the last check, used only when this client has Enforce off and the server refuses it anyway.
        static List<string> lastMissing;
        static int lastServerCount;
        static bool lastRepeat;

        // Builds the message box text, writes the report and leaves the world; the message box appears at the main menu.
        static void ShowRefusal(int serverCount, List<string> missing, bool repeatLoad)
        {
            popupHappened = (repeatLoad ? "A mod failed to load. " : "A mod crashed during startup. ") +
                "You were disconnected to protect the world. The server has " + missing.Count +
                (missing.Count == 1 ? " object" : " objects") + " your game failed to load (e.g. " + Examples(missing) + ".";
            if (repeatLoad)
                popupHappened += " This is world load #" + scenesBuilt + " in this game session. Some mods only register their objects the first time a world loads, " +
                    "so a retry after a timed-out or failed join, or after leaving another world, can come up short.";
            popupToDo = repeatLoad
                ? "Close the game completely, start it again, and join the server."
                : FirstLoadToDo();
            popupDetails = WriteReport(serverCount, missing) ? "Details: " + BepInExPath("PrefabGuard", "missing-prefabs.log") : null;
            popupMessage = ComposeBody(false, null); // plain text; also the signal that a message box is waiting to be shown
            Game.instance.Logout(false, true);
        }

        // The server tells a client it is about to be disconnected, so a client with Enforce off can still explain why.
        internal static void OnRefused(ZRpc rpc, int missingCount)
        {
            Instance.Logger.LogWarning("[PrefabGuard] The server refused this client (" + missingCount + " missing prefabs).");
            if (popupMessage != null || ourPopup != null || lastMissing == null || lastMissing.Count == 0 || Game.instance == null) return;
            var missing = lastMissing; lastMissing = null;
            ShowRefusal(lastServerCount, missing, lastRepeat);
        }

        // Detailed version of the popup: BepInEx\PrefabGuard\missing-prefabs.log. The first refusal in a game session
        // replaces the file (so it is not wiped by simply starting the game after a refusal); later refusals in the
        // same session are added below. Failing to write never stops the disconnect.
        static bool WriteReport(int serverCount, List<string> missing)
        {
            try
            {
                var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "PrefabGuard");
                var path = Path.Combine(dir, "missing-prefabs.log");
                Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                if (!reportStarted)
                {
                    sb.AppendLine("PrefabGuard report for the game session that started " + sessionStart.ToString("yyyy-MM-dd HH:mm:ss") + ".");
                    sb.AppendLine("This file is replaced the first time a join is refused in each game session. Later refusals in the same session are added below.");
                    sb.AppendLine();
                }
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
                sb.AppendLine("You were disconnected to protect the world.");
                sb.AppendLine("PrefabGuard version: " + Instance.Info.Metadata.Version);
                sb.AppendLine("Valheim version: " + global::Version.GetVersionString());
                sb.AppendLine("World load in this game session: #" + scenesBuilt);
                sb.AppendLine("Prefab names the server sent: " + serverCount);
                sb.AppendLine("Missing here: " + missing.Count);
                sb.AppendLine(scenesBuilt > 1
                    ? "Likely cause: this is not the first world load in this game session. Some mods register their objects only on the first load. Close the game completely, start it again, and join the server."
                    : "Likely cause: a mod failed during startup. Close the game, then check " + BepInExPath("LogOutput.log") + " for the first [Error] after \"Loading [...]\".");
                sb.AppendLine();
                sb.AppendLine("Missing prefabs (" + missing.Count + "):");
                foreach (var n in missing) sb.AppendLine("  " + n);
                sb.AppendLine();
                var plugins = BepInEx.Bootstrap.Chainloader.PluginInfos.Values
                    .OrderBy(i => i.Metadata.Name, StringComparer.OrdinalIgnoreCase).ToList();
                sb.AppendLine("Loaded plugins (" + plugins.Count + "):");
                foreach (var i in plugins) sb.AppendLine("  " + i.Metadata.Name + " " + i.Metadata.Version);
                sb.AppendLine();

                if (!reportStarted) { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); reportStarted = true; }
                else File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                Instance.Logger.LogInfo("[PrefabGuard] Wrote details to " + path);
                reportPath = path;
                return true;
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not write the report file: " + e.Message); return false; }
        }

        // Example names for the popup: the first 4 names joined by ", ". If that text is longer than 50 characters it is
        // cut to 47 and "..." is added (50 in total), which also shows the last name may be incomplete. "..." appears
        // only in that case; otherwise the list ends with ".". Returns the text up to and including the closing ")";
        // the caller adds the "." that ends the sentence.
        const int MaxExamplesLength = 50;

        static string Examples(List<string> missing)
        {
            string list = string.Join(", ", missing.Take(4).ToArray());
            list = list.Length > MaxExamplesLength ? list.Substring(0, MaxExamplesLength - 3) + "..." : list + ".";
            return list + ")";
        }

        // "What Happened:" / "What to do:" sections plus the Details line. With rich = true the headings get the title colour
        // and a larger size (rich text); otherwise plain text, for the vanilla fallback popup.
        static string ComposeBody(bool rich, string color)
        {
            return ComposeBodyFrom(rich, color, popupHappened, popupToDo, popupDetails);
        }

        static string ComposeBodyFrom(bool rich, string color, string happened, string toDo, string details)
        {
            if (!rich)
            {
                string plain = "What Happened:\n" + happened + "\n\nWhat to do:\n" + toDo;
                if (details != null) plain += "\n\n" + details;
                return plain;
            }
            // A blank line under the title, then the two sections.
            Func<string, string> heading = t => "<size=115%><color=#" + color + ">" + t + "</color></size>";
            string body = "\n" + heading("What Happened:") + "\n" + happened + "\n\n" + heading("What to do:") + "\n" + toDo;
            if (details != null) body += "\n\n" + details;
            return body;
        }

        // The wording of the shortest message (one missing object, first world load). Its text size is the size every
        // popup uses, so a longer message gets a taller box instead of smaller text.
        const string ReferenceHappened = "A mod crashed during startup. You were disconnected to protect the world. The server has 1 object your game failed to load (e.g. lower_v2.).";

        static string FirstLoadToDo()
        {
            return "Close the game, then check " + BepInExPath("LogOutput.log") + " for the first [Error] after \"Loading [...]\".";
        }

        // Keeps the text at the size the short message gets (the popup shrinks text to fit a fixed box), measures how tall
        // the real text is at that size, and makes the box taller by the difference.
        // The screen's size in the message box's own units. The box's on-screen scale (lossyScale) is how many pixels one
        // unit is, so units = pixels / scale. This works whatever the canvas looks like above the box.
        static bool ScreenSizeInUnits(Transform box, out float width, out float height)
        {
            width = 0f; height = 0f;
            Vector3 scale = box.lossyScale;
            if (scale.x <= 0.0001f || scale.y <= 0.0001f || Screen.width < 100 || Screen.height < 100) return false;
            width = Screen.width / scale.x;
            height = Screen.height / scale.y;
            return true;
        }

        const float MaxNormalFontSize = 40f;
        // The message box may use at most this much of the screen's height and width.
        const float ScreenFillFraction = 0.9f;

        static void FitBoxToText(Component body, Transform popupRoot, string headingColor)
        {
            try
            {
                var tb = Traverse.Create(body);
                var bodyRect = body.transform as RectTransform;
                if (bodyRect == null) return;

                // 0. What the game set up for this text, read before anything is changed: whether it shrinks to fit, and the
                // smallest and largest sizes it may use. The largest is the game's normal size for this text; shrinking only
                // happens when a message doesn't fit.
                bool wasAuto = false; float serialized = 0f, autoMin = 0f, autoMax = 0f;
                try
                {
                    wasAuto = tb.Property("enableAutoSizing").GetValue<bool>();
                    serialized = tb.Property("fontSize").GetValue<float>();
                    autoMin = tb.Property("fontSizeMin").GetValue<float>();
                    autoMax = tb.Property("fontSizeMax").GetValue<float>();
                }
                catch { }
                // A sanity limit, in case the game left the largest size at a huge default.
                float designed = (wasAuto && autoMax >= 1f) ? Math.Min(autoMax, MaxNormalFontSize) : serialized;

                // 1. The size the game's shrinking picks for the short message in this box (kept for the log).
                string reference = ComposeBodyFrom(true, headingColor, ReferenceHappened, FirstLoadToDo(),
                    "Details: " + BepInExPath("PrefabGuard", "missing-prefabs.log"));
                string actual = ComposeBody(true, headingColor);
                tb.Property("enableAutoSizing").SetValue(true);
                tb.Property("text").SetValue(reference);
                tb.Method("ForceMeshUpdate", new Type[] { typeof(bool), typeof(bool) }, new object[] { true, true }).GetValue();
                float measured = tb.Property("fontSize").GetValue<float>();
                float baseSize = designed >= 1f ? designed : measured;
                float size = baseSize * Math.Max(0.5f, MessageBoxTextSizeFactor.Value);
                Instance.Logger.LogInfo("[PrefabGuard] MsgBox game text settings: shrink to fit " + wasAuto + ", sizes " + autoMin.ToString("0.0") + " to " +
                    autoMax.ToString("0.0") + ", set size " + serialized.ToString("0.0") + "; using " + baseSize.ToString("0.0") + " as the game's normal size");

                // 2. The real text at that fixed size, and how tall it needs to be.
                tb.Property("enableAutoSizing").SetValue(false);
                tb.Property("fontSize").SetValue(size);
                tb.Property("text").SetValue(actual);
                tb.Method("ForceMeshUpdate", new Type[] { typeof(bool), typeof(bool) }, new object[] { true, true }).GetValue();
                float needed = tb.Property("preferredHeight").GetValue<float>();
                float have = bodyRect.rect.height;
                float extra = needed - have;
                Instance.Logger.LogInfo("[PrefabGuard] MsgBox text: size " + size.ToString("0.0") + " (the game's own " + baseSize.ToString("0.0") + " x " + MessageBoxTextSizeFactor.Value.ToString("0.##") + "; the game's shrinking gave the short message " + measured.ToString("0.0") + "), needs " + needed.ToString("0") +
                    " high, box text area is " + have.ToString("0") + " high, growing the box by " + Math.Max(0f, extra).ToString("0"));
                if (extra <= 1f) return;

                // 2b. The box must fit on the screen, or the title and buttons end up off-screen. If it would be too tall,
                // make the text smaller (like the game's own shrinking, but only as much as needed) until it fits.
                RectTransform outer = null; // the outermost fixed-height box, the one that is drawn as the message box
                for (Transform t = body.transform; t != null && t != popupRoot; t = t.parent)
                {
                    var r = t as RectTransform;
                    if (r != null && r.anchorMin.y == r.anchorMax.y) outer = r;
                }
                float screenW, screenH;
                if (outer == null || !ScreenSizeInUnits(popupRoot, out screenW, out screenH))
                    Instance.Logger.LogWarning("[PrefabGuard] MsgBox: could not work out the screen size, so the box is not limited to the screen.");
                else
                {
                    float maxExtra = Math.Max(0f, screenH * ScreenFillFraction - outer.rect.height);
                    float startSize = size;
                    Instance.Logger.LogInfo("[PrefabGuard] MsgBox screen: " + Screen.width + " x " + Screen.height + " pixels, " + screenW.ToString("0") + " x " +
                        screenH.ToString("0") + " in the box's units; the box may grow by at most " + maxExtra.ToString("0"));
                    for (int i = 0; i < 12 && extra > maxExtra + 1f; i++)
                    {
                        float shrink = (float)Math.Sqrt((have + maxExtra) / Math.Max(1f, needed)) * 0.97f;
                        size = Math.Max(1f, size * Math.Min(0.98f, shrink));
                        tb.Property("fontSize").SetValue(size);
                        tb.Method("ForceMeshUpdate", new Type[] { typeof(bool), typeof(bool) }, new object[] { true, true }).GetValue();
                        needed = tb.Property("preferredHeight").GetValue<float>();
                        extra = needed - have;
                    }
                    if (size < startSize)
                        Instance.Logger.LogInfo("[PrefabGuard] MsgBox would be taller than the screen (" + screenH.ToString("0") +
                            " high), so the text size was reduced from " + startSize.ToString("0.0") + " to " + size.ToString("0.0") +
                            "; it now needs " + needed.ToString("0") + " high, growing the box by " + Math.Max(0f, extra).ToString("0"));
                    if (extra <= 1f) return;
                }

                // 3. Grow each fixed-height box around the text; boxes that stretch with their parent follow on their own.
                for (Transform t = body.transform; t != null && t != popupRoot; t = t.parent)
                {
                    var rt = t as RectTransform;
                    if (rt == null) continue;
                    bool stretched = rt.anchorMin.y != rt.anchorMax.y;
                    Instance.Logger.LogInfo("[PrefabGuard] MsgBox part " + rt.name + ": height " + rt.rect.height.ToString("0") +
                        (stretched ? " (stretches with its parent, left alone)" : ", growing by " + extra.ToString("0")));
                    if (!stretched) rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + extra);
                }
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not fit the MsgBox to the text: " + e.Message); }
        }

        static void ShowPopup()
        {
            if (popupMessage == null || ZNetScene.instance != null) return;
            try
            {
                if (!UnifiedPopup.IsAvailable()) return;
                var msg = popupMessage; popupMessage = null;
                var title = PopupTitle;
                bool withReport = reportPath != null && File.Exists(reportPath);
                ReloadConfig();
                if (DebugForceVanillaMessageBox.Value)
                {
                    // Testing: show the game's own plain one-button message box, the one used when the copy can't be built.
                    Instance.Logger.LogInfo("[PrefabGuard] ForceVanillaMessageBox is on, showing the game's plain MsgBox.");
                    UnifiedPopup.Push(new WarningPopup(title, msg, delegate { UnifiedPopup.Pop(); }, true));
                    return;
                }
                try { ourPopup = BuildPopup(title, msg, withReport); }
                catch (Exception e)
                {
                    Instance.Logger.LogWarning("[PrefabGuard] Could not build the MsgBox copy (" + e.Message + "), using the plain MsgBox.");
                    ClosePopup();
                    UnifiedPopup.Push(new WarningPopup(title, msg, delegate { UnifiedPopup.Pop(); }, true));
                }
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not show the MsgBox: " + e.Message); popupMessage = null; }
        }

        // ---- Our own popup ----
        // Valheim reuses ONE popup UI for every message. To stay out of its way, we make a copy of that popup's UI objects
        // (same look, same canvas), fill in our text, wire our own buttons, and destroy the copy when it closes. Nothing on
        // the shared popup is changed. If the copy can't be built, the plain vanilla one-button popup is used instead.

        static GameObject ourPopup;

        // Finds the object in `dstRoot` that sits at the same place as `src` does under `srcRoot`.
        static Transform FindMatching(Transform srcRoot, Transform src, Transform dstRoot)
        {
            if (src == null) return null;
            var path = new List<int>();
            Transform t = src;
            for (; t != null && t != srcRoot; t = t.parent) path.Add(t.GetSiblingIndex());
            if (t != srcRoot) return null; // not under the popup root
            Transform d = dstRoot;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i] >= d.childCount) return null;
                d = d.GetChild(path[i]);
            }
            return d;
        }

        static void SetText(Component textComponent, string text)
        {
            if (textComponent != null) Traverse.Create(textComponent).Property("text").SetValue(text);
        }

        static void WireButton(Component button, Action action)
        {
            if (button == null) return;
            var ev = Traverse.Create(button).Property("onClick").GetValue<UnityEngine.Events.UnityEvent>();
            if (ev == null) return;
            ev.RemoveAllListeners();
            ev.AddListener(new UnityEngine.Events.UnityAction(action));
        }

        // Our buttons, left to right, and what each does. Used for keyboard and controller navigation.
        static readonly List<GameObject> ourButtons = new List<GameObject>();
        static readonly List<Action> ourButtonActions = new List<Action>();
        static float lastOpenTime = -10f;

        // Registers a button for navigation: hovering it with the mouse highlights it (and leaving removes the highlight),
        // and the game's own arrow/stick navigation is turned off for it so only ours moves the highlight.
        static void AddNavButton(Component c, Action action)
        {
            if (c == null) return;
            var sel = c as UnityEngine.UI.Selectable;
            if (sel != null)
            {
                var nav = sel.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                sel.navigation = nav;
            }
            if (c.gameObject.GetComponent<HoverSelect>() == null) c.gameObject.AddComponent<HoverSelect>();
            ourButtons.Add(c.gameObject);
            ourButtonActions.Add(action);
        }

        // BepInEx doesn't watch a config file for edits while the game runs, so read it again each time PrefabGuard is
        // about to use its settings. A change made in the file (or the in-game settings menu) applies to the next join
        // without restarting the game. PrefabGuard doesn't write the file while doing this (see below).
        static void ReloadConfig()
        {
            // BepInEx saves the config file whenever a setting's value changes, including changes made by re-reading the file.
            // That would rewrite your edited file after every test (and editors like Notepad++ report it), so saving is
            // switched off for the re-read and put back right after.
            var config = Instance.Config;
            bool saveOnSet = config.SaveOnConfigSet;
            try
            {
                config.SaveOnConfigSet = false;
                config.Reload();
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not re-read the config file: " + e.Message); }
            finally { config.SaveOnConfigSet = saveOnSet; }
        }

        static GameObject BuildPopup(string title, string msg, bool withReport)
        {
            ReloadConfig();
            var inst = Traverse.Create(typeof(UnifiedPopup)).Field("instance").GetValue();
            if (inst == null) throw new Exception("MsgBox instance not found");
            var tr = Traverse.Create(inst);
            var src = tr.Field("popupUIParent").GetValue<GameObject>();
            if (src == null) throw new Exception("MsgBox UI not found");

            var clone = UnityEngine.Object.Instantiate(src, src.transform.parent);
            clone.name = "PrefabGuardPopup";

            // The same object, but inside the copy.
            Func<string, Transform> find = field =>
            {
                var o = tr.Field(field).GetValue();
                Transform st = null;
                var go = o as GameObject; if (go != null) st = go.transform;
                var c = o as Component; if (c != null) st = c.transform;
                return FindMatching(src.transform, st, clone.transform);
            };
            Func<string, Component> comp = field =>
            {
                var o = tr.Field(field).GetValue() as Component;
                var t = find(field);
                return (o == null || t == null) ? null : t.GetComponent(o.GetType());
            };
            Action<string, bool> show = (field, on) => { var t = find(field); if (t != null) t.gameObject.SetActive(on); };

            // Only the buttons we use: Open report + OK, or just OK when there is no report.
            show("buttonConfirm", false);
            show("textEntryField", false);
            show("buttonLeft", withReport);
            show("buttonRight", withReport);
            show("buttonCenter", !withReport);
            show("fullScreenBackgroundCover", true); // keeps clicks from reaching the menu behind

            // The section headings use the title's colour, a little larger than the body text.
            string headingColor = "FFFFFF";
            try { headingColor = ColorUtility.ToHtmlStringRGB(Traverse.Create(tr.Field("headerText").GetValue()).Property("color").GetValue<Color>()); } catch { }
            SetText(comp("headerText"), title);
            SetText(comp("bodyText"), ComposeBody(true, headingColor));
            // In Valheim's popup the object named "buttonRight" is drawn on the LEFT and "buttonLeft" on the right
            // (seen in-game), so "Open report" goes on buttonRight to appear on the left.
            SetText(comp("buttonRightText"), OpenReportLabel);
            SetText(comp("buttonLeftText"), CloseLabel);
            SetText(comp("buttonCenterText"), CloseLabel);

            WireButton(comp("buttonRight"), OpenReport);  // opens the file, the popup stays up
            WireButton(comp("buttonLeft"), ClosePopup);
            WireButton(comp("buttonCenter"), ClosePopup);

            var body = comp("bodyText");
            float f = MessageBoxWidthFactor.Value;
            if (body != null && f > 1.001f)
            {
                float screenW, screenH;
                bool haveScreen = ScreenSizeInUnits(clone.transform, out screenW, out screenH);
                for (Transform t = body.transform; t != null && t != clone.transform; t = t.parent)
                {
                    var rt = t as RectTransform;
                    if (rt == null) continue;
                    bool stretched = rt.anchorMin.x != rt.anchorMax.x;
                    float widen = f;
                    // Never wider than the screen allows.
                    if (!stretched && haveScreen && rt.rect.width > 1f && rt.rect.width * widen > screenW * ScreenFillFraction)
                        widen = Math.Max(1f, screenW * ScreenFillFraction / rt.rect.width);
                    Instance.Logger.LogInfo("[PrefabGuard] MsgBox part " + rt.name + ": width " + rt.rect.width.ToString("0") +
                        (stretched ? " (stretches with its parent, left alone)" : ", widening x" + widen.ToString("0.##") +
                        (widen < f ? " (limited by the screen width)" : "")));
                    if (!stretched) rt.sizeDelta = new Vector2(rt.sizeDelta.x * widen, rt.sizeDelta.y);
                }
            }

            // The game's own controller button hints (the A/B prompts next to its buttons) show broken text in the copy
            // ("missing button def ..."), and its A/B handling would run alongside ours. Switch both off in the copy only;
            // our keys and controller buttons are handled in TickPopup.
            foreach (var pad in clone.GetComponentsInChildren<UIGamePad>(true))
            {
                if (pad.m_hint != null) pad.m_hint.SetActive(false);
                pad.enabled = false;
            }

            // Navigation order is left to right: "Open report", then "OK" (the "buttonRight" object is drawn on the left).
            ourButtons.Clear(); ourButtonActions.Clear();
            if (withReport) { AddNavButton(comp("buttonRight"), OpenReport); AddNavButton(comp("buttonLeft"), ClosePopup); }
            else AddNavButton(comp("buttonCenter"), ClosePopup);

            clone.SetActive(true);
            clone.transform.SetAsLastSibling();
            // MessageBoxTextSizeFactor = 0 keeps the game's fixed-height box and its shrink-to-fit text.
            if (body != null && MessageBoxTextSizeFactor.Value > 0.001f) FitBoxToText(body, clone.transform, headingColor);
            // Nothing is highlighted when it opens. The mouse, the arrow keys, Tab or the controller highlight a button.
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null) es.SetSelectedGameObject(null);
            return clone;
        }

        static void ClosePopup()
        {
            if (ourPopup != null) UnityEngine.Object.Destroy(ourPopup);
            ourPopup = null;
            ourButtons.Clear(); ourButtonActions.Clear();
        }

        // Starts a program with one argument (the file), without a shell. Returns false if it could not be started.
        static bool TryStart(string program, string argument)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(program, "\"" + argument + "\"") { UseShellExecute = false, CreateNoWindow = true };
                return System.Diagnostics.Process.Start(psi) != null;
            }
            catch { return false; }
        }

        // Opens the report in the system's default program: the shell on Windows, xdg-open on Linux, open on macOS.
        // If that fails, tries the game's own file-link opener with a proper file:// address.
        static void OpenReport()
        {
            // Enter or A can reach this twice (the game's own button handling and ours), so ignore a second call.
            if (Time.unscaledTime - lastOpenTime < 0.5f) return;
            lastOpenTime = Time.unscaledTime;
            if (reportPath == null || !File.Exists(reportPath)) return;

            bool opened = false;
            try
            {
                var platform = Application.platform;
                if (platform == RuntimePlatform.LinuxPlayer || platform == RuntimePlatform.LinuxEditor)
                    opened = TryStart("xdg-open", reportPath);
                else if (platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor)
                    opened = TryStart("open", reportPath);
                else
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(reportPath) { UseShellExecute = true };
                    opened = System.Diagnostics.Process.Start(psi) != null;
                }
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not open the report with the default program (" + e.Message + ")."); }
            if (opened) return;

            try { Application.OpenURL(new Uri(reportPath).AbsoluteUri); }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not open the report: " + e.Message); }
        }

        // A path under the BepInEx folder, written with this system's own separator, for text shown to the player.
        static string BepInExPath(params string[] parts)
        {
            return "BepInEx" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        // Keyboard and controller:
        //   Escape or controller B   click OK (close), whatever is highlighted
        //   Right/Down/Tab, d-pad or stick right/down   highlight the next button (the first one if none is highlighted)
        //   Left/Up/Shift+Tab, d-pad or stick left/up   highlight the previous button
        //   Enter, Space or controller A   click the highlighted button (nothing happens if none is highlighted)
        // The mouse highlights the button it is over and clears the highlight when it leaves.
        static void TickPopup()
        {
            if (ourPopup == null) return;
            try
            {
                if (ZInput.GetButtonDown("JoyButtonB") || ZInput.GetKeyDown(KeyCode.Escape)) { ClosePopup(); return; }

                var es = UnityEngine.EventSystems.EventSystem.current;
                int n = ourButtons.Count;
                if (es == null || n == 0) return;
                int idx = ourButtons.IndexOf(es.currentSelectedGameObject);
                bool shift = ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift);

                bool next = ZInput.GetKeyDown(KeyCode.RightArrow) || ZInput.GetKeyDown(KeyCode.DownArrow) ||
                            (ZInput.GetKeyDown(KeyCode.Tab) && !shift) ||
                            ZInput.GetButtonDown("JoyDPadRight") || ZInput.GetButtonDown("JoyDPadDown") ||
                            ZInput.GetButtonDown("JoyLStickRight") || ZInput.GetButtonDown("JoyLStickDown");
                bool prev = ZInput.GetKeyDown(KeyCode.LeftArrow) || ZInput.GetKeyDown(KeyCode.UpArrow) ||
                            (ZInput.GetKeyDown(KeyCode.Tab) && shift) ||
                            ZInput.GetButtonDown("JoyDPadLeft") || ZInput.GetButtonDown("JoyDPadUp") ||
                            ZInput.GetButtonDown("JoyLStickLeft") || ZInput.GetButtonDown("JoyLStickUp");
                if (next || prev)
                {
                    int target = idx < 0 ? 0 : next ? (idx + 1) % n : (idx - 1 + n) % n;
                    es.SetSelectedGameObject(ourButtons[target]);
                    return;
                }

                bool activate = ZInput.GetKeyDown(KeyCode.Return) || ZInput.GetKeyDown(KeyCode.KeypadEnter) ||
                                ZInput.GetKeyDown(KeyCode.Space) || ZInput.GetButtonDown("JoyButtonA");
                if (activate && idx >= 0) ourButtonActions[idx]();
            }
            catch { }
        }

        internal static ZNetPeer PeerOf(ZNet znet, ZRpc rpc)
        {
            foreach (var p in znet.GetPeers()) if (p.m_rpc == rpc) return p;
            return null;
        }
    }

    // On a popup button: the mouse entering it highlights it, and leaving it clears the highlight (if it is still the
    // highlighted one). The highlight is the UI's "selected" button, so the keyboard and the mouse share one highlight.
    class HoverSelect : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData e)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null) es.SetSelectedGameObject(gameObject);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData e)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject == gameObject) es.SetSelectedGameObject(null);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    static class ZNetScene_Awake
    {
        static void Postfix() { PrefabGuardPlugin.scenesBuilt++; }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    static class ZNet_OnNewConnection
    {
        static void Postfix(ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>("PrefabGuard_Names", PrefabGuardPlugin.OnNames);
            peer.m_rpc.Register<int, string>("PrefabGuard_Ack", PrefabGuardPlugin.OnAck);
            peer.m_rpc.Register<int>("PrefabGuard_Refused", PrefabGuardPlugin.OnRefused);
        }
    }

    // The server has the client's full peer info (password, version checks done) at this point.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    static class ZNet_RPC_PeerInfo
    {
        static void Postfix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return;
            var peer = PrefabGuardPlugin.PeerOf(__instance, rpc);
            if (peer != null) PrefabGuardPlugin.SendNames(__instance, peer);
        }
    }
}

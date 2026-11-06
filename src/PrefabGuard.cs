using System;
using System.Collections.Generic;
using System.Linq;
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
    [BepInPlugin(Id, "PrefabGuard", "1.0.0")]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    public class PrefabGuardPlugin : BaseUnityPlugin
    {
        public const string Id = "scooby_drew902.valheim.prefabguard";
        const string RpcNames = "PrefabGuard_Names";
        const string RpcAck = "PrefabGuard_Ack";

        internal static PrefabGuardPlugin Instance;
        internal static ConfigEntry<bool> Enforce;
        internal static ConfigEntry<float> GraceSeconds;
        internal static ConfigEntry<int> MaxMissing;
        internal static ConfigEntry<string> IgnoreContains;
        internal static ConfigEntry<string> DebugPretendMissing;

        // Server: peer uid -> time the server sent the list, until the client answers.
        static readonly Dictionary<long, float> Waiting = new Dictionary<long, float>();
        // Client: names received from the server, waiting for ZNetScene to exist.
        static List<string> pendingNames;
        static ZRpc pendingRpc;
        static float sceneSeenAt = -1f;
        static string popupMessage;

        void Awake()
        {
            Instance = this;
            Enforce = Config.Bind("General", "Enforce", true,
                "true: kick/leave when prefabs are missing. false: only log (use to test for false positives).");
            GraceSeconds = Config.Bind("General", "GraceSeconds", 60f,
                "Server: seconds a client has to answer before it is kicked.");
            MaxMissing = Config.Bind("General", "MaxMissing", 0,
                "How many server prefabs a client may lack before it is refused.");
            IgnoreContains = Config.Bind("General", "IgnoreContains", "",
                "Comma-separated name fragments; prefabs whose name contains one are not checked.");
            DebugPretendMissing = Config.Bind("Debug", "PretendMissing", "",
                "Testing only, leave empty: comma-separated exact prefab names this client pretends not to have.");
            new Harmony(Id).PatchAll(typeof(PrefabGuardPlugin).Assembly);
            Logger.LogInfo("PrefabGuard loaded. Enforce=" + Enforce.Value);
        }

        static bool Ignored(string name)
        {
            foreach (var frag in IgnoreContains.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.IndexOf(frag.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        void Update()
        {
            var znet = ZNet.instance;
            if (znet == null) { ShowPopup(); return; }

            if (znet.IsServer()) ServerTick(znet);
            else ClientTick();
            ShowPopup();
        }

        // ---------------- server ----------------

        static void ServerTick(ZNet znet)
        {
            if (Waiting.Count == 0) return;
            foreach (var uid in Waiting.Keys.ToList())
            {
                var peer = znet.GetPeer(uid);
                if (peer == null) { Waiting.Remove(uid); continue; }
                if (Time.realtimeSinceStartup - Waiting[uid] < GraceSeconds.Value) continue;
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
            if (missing <= MaxMissing.Value)
            {
                Instance.Logger.LogInfo("[PrefabGuard] " + peer.m_playerName + " passed (" + missing + " missing).");
                return;
            }
            Instance.Logger.LogWarning("[PrefabGuard] " + peer.m_playerName + " is missing " + missing + " prefabs, e.g. " + sample +
                (Enforce.Value ? ". Disconnecting." : ". Enforce is off, allowing."));
            if (Enforce.Value) znet.Disconnect(peer);
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

            var names = pendingNames; var rpc = pendingRpc;
            pendingNames = null; pendingRpc = null;
            var fake = DebugPretendMissing.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            var missing = names.Where(n => scene.GetPrefab(n) == null || fake.Contains(n)).ToList();
            string sample = string.Join(", ", missing.Take(8).ToArray());
            Instance.Logger.LogInfo("[PrefabGuard] Checked " + names.Count + " prefabs, " + missing.Count + " missing.");
            try { rpc.Invoke(RpcAck, new object[] { missing.Count, sample }); } catch { }

            if (missing.Count <= MaxMissing.Value) return;
            Instance.Logger.LogError("[PrefabGuard] " + missing.Count + " prefabs the server has are missing here: " + string.Join(", ", missing.Take(40).ToArray()));
            if (!Enforce.Value) { Instance.Logger.LogWarning("[PrefabGuard] Enforce is off, staying connected."); return; }
            popupMessage = "The server has " + missing.Count + " objects your game failed to load (e.g. " + sample + "). " +
                "A mod crashed during startup. You were disconnected to protect the world. " +
                "Close the game, then check BepInEx\\LogOutput.log for the first [Error] after \"Loading [...]\".";
            Game.instance.Logout(false, true);
        }

        static void ShowPopup()
        {
            if (popupMessage == null || ZNetScene.instance != null) return;
            try
            {
                if (!UnifiedPopup.IsAvailable()) return;
                var msg = popupMessage; popupMessage = null;
                UnifiedPopup.Push(new WarningPopup("PrefabGuard: mods failed to load", msg, delegate { UnifiedPopup.Pop(); }, true));
            }
            catch (Exception e) { Instance.Logger.LogWarning("[PrefabGuard] Could not show popup: " + e.Message); popupMessage = null; }
        }

        internal static ZNetPeer PeerOf(ZNet znet, ZRpc rpc)
        {
            foreach (var p in znet.GetPeers()) if (p.m_rpc == rpc) return p;
            return null;
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    static class ZNet_OnNewConnection
    {
        static void Postfix(ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>("PrefabGuard_Names", PrefabGuardPlugin.OnNames);
            peer.m_rpc.Register<int, string>("PrefabGuard_Ack", PrefabGuardPlugin.OnAck);
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

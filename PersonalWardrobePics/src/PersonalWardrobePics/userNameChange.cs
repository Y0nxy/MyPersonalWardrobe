using HarmonyLib;
using Peak.Network;
using Photon.Pun;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Text;

namespace MyPersonalWardrobe
{
    public class userNameChange
    {
        public static string userName;
        public static void ApplyPatches()
        {
            var harmony = new Harmony("com.myusername.myproject");
            harmony.PatchAll();
        }
        public static void SetUserName(string newName)
        {
            userName = newName;
            if (PhotonNetwork.IsConnected && PhotonNetwork.InLobby)
            {
                Plugin.Log.LogInfo($"Changing name to: {newName}");
                PhotonNetwork.NickName = newName;
            }
        }
        public class NamePatches
        {
            [HarmonyPatch(typeof(NetworkConnector), "Start")]
            [HarmonyPostfix]
            public static void ChangeName(string newName)
            { 
                if (!PhotonNetwork.IsConnected || !PhotonNetwork.InLobby) return;
                Plugin.Log.LogInfo($"Changing name to: {newName}");
                PhotonNetwork.NickName = newName;
                userName = newName;
            }
            [HarmonyPatch(typeof(SteamFriends), "GetFriendPersonaName")]
            [HarmonyPrefix]
            private static bool GetFriendPersonaName_Prefix(CSteamID steamIDFriend, ref string __result)
            {
                Plugin.Log.LogInfo($"GetFriendPersonaName called for SteamID: {steamIDFriend}");
                __result = userName;
                return false; // Skip the original method
            }
        }
    }
}

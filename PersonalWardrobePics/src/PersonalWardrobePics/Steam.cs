using Photon.Pun;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Text;
using Photon.Realtime;

namespace TextChatCommands
{
    internal class Steam
    {
        // ─── Photon.Realtime.Player -> SteamID64 ─────────────────────────────────────

        public static bool TryGetSteamId(Photon.Realtime.Player photonPlayer, out ulong steamId)
        {
            steamId = 0UL;
            if (photonPlayer == null) return false;
            var id = ulong.TryParse(photonPlayer.UserId, out steamId);
            Plugin.logMessage(id+" id, steamid:" + steamId);
            return id;
        }

        public static bool TryGetCSteamID(Photon.Realtime.Player photonPlayer, out CSteamID id)
        {
            id = default(CSteamID);
            ulong raw;
            if (!TryGetSteamId(photonPlayer, out raw)) return false;
            id = new CSteamID(raw);
            return true;
        }

        // PEAK's own Player component -> SteamID64
        public static bool TryGetSteamId(global::Player peakPlayer, out ulong steamId)
        {
            steamId = 0UL;
            if (peakPlayer == null || peakPlayer.photonView == null) return false;
            return ulong.TryParse(peakPlayer.photonView.Owner?.UserId, out steamId);
        }

        // Character -> SteamID64
        public static bool TryGetSteamId(Character c, out ulong steamId)
        {
            steamId = 0UL;
            if (c == null) return false;
            PhotonView pv = c.photonView;
            if (pv == null || pv.Owner == null) return false;
            return ulong.TryParse(pv.Owner.UserId, out steamId);
        }

        // ─── SteamID64 -> Photon.Realtime.Player ─────────────────────────────────────

        public static Photon.Realtime.Player FromSteamId(ulong steamId)
        {
            return FromUserId(steamId.ToString());
        }

        public static Photon.Realtime.Player FromUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            Photon.Realtime.Player[] list = PhotonNetwork.PlayerList;   // includes the local player
            if (list == null) return null;
            for (int i = 0; i < list.Length; i++)
                if (list[i] != null && list[i].UserId == userId) return list[i];
            return null;
        }

        // ─── SteamID64 -> PEAK Player / Character (via the game's own registry) ──────

        // Returns null until the player is registered in PlayerHandler.
        // Early in a join, use FromSteamId() above - PhotonNetwork.PlayerList fills first.
        public static global::Player PeakPlayerFromSteamId(ulong steamId)
        {
            return PlayerHandler.GetPlayer(steamId.ToString());
        }

        public static Character CharacterFromSteamId(ulong steamId)
        {
            Photon.Realtime.Player p = FromSteamId(steamId);
            return p == null ? null : PlayerHandler.GetPlayerCharacter(p);
        }

        // ─── Our own id ──────────────────────────────────────────────────────────────

        // 0 means "not available" (Steam not running, or not logged on).
        // PhotonNetwork.LocalPlayer.UserId gives the same value without touching Steamworks.
        public static ulong LocalSteamId()
        {
            return (SteamAPI.IsSteamRunning() && SteamUser.BLoggedOn())
                 ? SteamUser.GetSteamID().m_SteamID
                 : 0UL;
        }

        // ─── Verify before you act ───────────────────────────────────────────────────

        // An id out of UserId is a CLAIM, not proof - the client supplies it at auth time and
        // AuthType is CustomAuthenticationType.None. Before kicking, banning, or persisting an
        // id, check it against Steam's real lobby roster. The game ships this check itself:
        //
        //     Peak.Network.SteamLobbyAPI.PlayerIsInLobby(string playerId)
        //
        // A `false` is not always a lie - a late Photon join or a stale member list produces one
        // legitimately. Treat it as "do not act yet", not as "caught someone".
    }
}

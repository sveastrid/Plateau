using System;
using System.Text;
using UnityEngine;

/// <summary>
/// What a joiner says about itself before it is let in: its name, its library, and the build it is
/// running.
///
/// This rides in NetworkManager.NetworkConfig.ConnectionData and is read by the server's
/// ConnectionApprovalCallback, which runs **before the client is synchronized into a scene**. That
/// is the whole reason to do it this way: the server can refuse with a reason the joiner can read,
/// and GameController.HandleClientDisconnect already surfaces NetworkManager.DisconnectReason in
/// the lobby, so "You do not own BASH" reaches the joiner's panel with no new plumbing.
///
/// Hand-encoded rather than JSON: it is three fields, it is on the hot path of a join that is
/// already the flakiest thing in the project, and a version byte at the front means a future field
/// is a readable failure rather than a garbled name.
///
/// Layout: [byte version][ulong mask][ushort len][utf8 name][ushort len][utf8 build]
/// </summary>
public struct ConnectionPayload
{
    public const byte Version = 1;

    /// <summary>
    /// Netcode caps ConnectionData well above this; the cap here exists so a modified client cannot
    /// make the server allocate on an unbounded length field.
    /// </summary>
    public const int MaxStringBytes = 128;

    public string playerName;
    public ulong ownedMask;
    public string build;

    public static ConnectionPayload ForLocalPlayer(string playerName)
    {
        return new ConnectionPayload
        {
            playerName = playerName ?? "",
            ownedMask = StoreService.OwnedMask,
            // Application.version, not a hash of anything: it is what the room directory publishes
            // and greys rows on, so the two must be the same string.
            build = Application.version ?? ""
        };
    }

    public byte[] Encode()
    {
        byte[] nameBytes = Trim(playerName);
        byte[] buildBytes = Trim(build);

        byte[] buffer = new byte[1 + 8 + 2 + nameBytes.Length + 2 + buildBytes.Length];
        int at = 0;

        buffer[at++] = Version;

        Buffer.BlockCopy(BitConverter.GetBytes(ownedMask), 0, buffer, at, 8);
        at += 8;

        at = WriteString(buffer, at, nameBytes);
        WriteString(buffer, at, buildBytes);

        return buffer;
    }

    /// <summary>
    /// Decode a payload sent by a client. Every length is checked against what is actually there:
    /// this is the one place in the project that parses bytes a stranger chose.
    /// </summary>
    public static bool TryDecode(byte[] data, out ConnectionPayload payload)
    {
        payload = new ConnectionPayload { playerName = "", build = "", ownedMask = 0UL };

        if (data == null || data.Length < 1 + 8 + 2 + 2)
        {
            return false;
        }

        int at = 0;
        if (data[at++] != Version)
        {
            return false;
        }

        payload.ownedMask = BitConverter.ToUInt64(data, at);
        at += 8;

        if (!ReadString(data, ref at, out payload.playerName))
        {
            return false;
        }
        if (!ReadString(data, ref at, out payload.build))
        {
            return false;
        }

        return true;
    }

    private static byte[] Trim(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length <= MaxStringBytes)
        {
            return bytes;
        }

        byte[] cut = new byte[MaxStringBytes];
        Buffer.BlockCopy(bytes, 0, cut, 0, MaxStringBytes);
        return cut;
    }

    private static int WriteString(byte[] buffer, int at, byte[] bytes)
    {
        buffer[at++] = (byte)(bytes.Length & 0xFF);
        buffer[at++] = (byte)((bytes.Length >> 8) & 0xFF);
        Buffer.BlockCopy(bytes, 0, buffer, at, bytes.Length);
        return at + bytes.Length;
    }

    private static bool ReadString(byte[] data, ref int at, out string value)
    {
        value = "";

        if (at + 2 > data.Length)
        {
            return false;
        }

        int length = data[at] | (data[at + 1] << 8);
        at += 2;

        if (length < 0 || length > MaxStringBytes || at + length > data.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(data, at, length);
        at += length;
        return true;
    }
}

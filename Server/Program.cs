#nullable enable // Enable nullable reference types checking for this file

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class SocketServer
{
    // Use EndPoint (IP + Port) as the key instead of Socket
    public static Dictionary<EndPoint, PlayerInfo> clients = new();
    // Declare serverSocket as nullable to address CS8618
    private static Socket? serverSocket;

    static async Task Main()
    {
        int port = 9843; // Change as needed
        IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, port);

        // Create a single UDP socket
        serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            serverSocket.Bind(endPoint);
            Console.WriteLine($"Server started on port {port}. Waiting for datagrams...");

            byte[] buffer = new byte[1024]; // Buffer for receiving data

            // Main receive loop
            while (true)
            {
                // Ensure serverSocket is not null before receiving
                if (serverSocket == null)
                {
                    Console.WriteLine("Server socket is null, exiting receive loop.");
                    break;
                }
                // Receive data and capture the sender's endpoint
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                SocketReceiveFromResult result = await serverSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP);

                // Process the received datagram
                HandleDatagram(buffer, result.ReceivedBytes, remoteEP);
            }
        }
        catch (ObjectDisposedException)
        {
             // Expected when the socket is closed while ReceiveFromAsync is pending
             Console.WriteLine("Server socket closed, shutting down.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
        }
        finally
        {
            // Use null-conditional operator for safe disposal
            serverSocket?.Close();
        }
    }

    private static void HandleDatagram(byte[] buffer, int bytesRead, EndPoint remoteEP)
    {
        // If it's a new client, assign an ID and add them
        if (!clients.ContainsKey(remoteEP))
        {
            ushort id = 0x0000;
            List<ushort> usedIds = PlayerInfo.usedIds();
            for (ushort idPossibility = 0x0000; idPossibility <= 0xFFFF; idPossibility++)
            {
                if (!usedIds.Contains(idPossibility))
                {
                    id = idPossibility;
                    break;
                }
            }

            PlayerInfo info = new PlayerInfo { userId = id };
            clients.Add(remoteEP, info);
            Console.WriteLine($"Client connected: {remoteEP}. Assigning ID: {id}");
        }

        // Get the PlayerInfo associated with the sender's endpoint
        // Use TryGetValue for safer access
        if (!clients.TryGetValue(remoteEP, out PlayerInfo? senderInfo) || senderInfo == null)
        {
             Console.WriteLine($"Warning: Could not find PlayerInfo for sender {remoteEP}.");
             return; // Cannot process if sender info is missing
        }


        // Create a byte array containing only the received data
        byte[] receivedData = new byte[bytesRead];
        Array.Copy(buffer, 0, receivedData, 0, bytesRead);

        // No MemoryStream or length prefix handling needed for UDP
        try
        {
            byte[] toSend;
            switch (receivedData[0]) // Packet ID is the first byte
            {
                case 0x03: // Name Packet
                    senderInfo.username = Encoding.UTF8.GetString(receivedData, 1, receivedData.Length - 1);
                    Console.WriteLine($"{senderInfo.userId} ({remoteEP}) username set to {senderInfo.username}");

                    // Prepare packet to broadcast: [PacketID, UserID_Hi, UserID_Lo, Name...]
                    toSend = new byte[receivedData.Length + 2];
                    toSend[0] = receivedData[0]; // Packet ID
                    toSend[1] = (byte)(senderInfo.userId >> 8);
                    toSend[2] = (byte)(senderInfo.userId & 0xFF);
                    Array.Copy(receivedData, 1, toSend, 3, receivedData.Length - 1); // Copy Name

                    // Broadcast to other clients
                    Broadcast(toSend, remoteEP);
                    break;

                case 0x04: // Get Name Packet
                    ushort requestedId = (ushort)((receivedData[1] << 8) | receivedData[2]);
                    // Declare info as nullable and check result to address CS8600
                    PlayerInfo? info = PlayerInfo.FindById(requestedId);
                    string username = $"no username for {requestedId}";
                    // Check if info is not null before accessing username
                    if (info != null && !string.IsNullOrEmpty(info.username))
                    {
                        username = info.username;
                    }
                    // Ensure not empty (already handled but good practice)
                    if (string.IsNullOrEmpty(username)) username = "no username sent";
                    byte[] usernameBytes = Encoding.UTF8.GetBytes(username);
                    // Prepare packet to send back: [PacketID, Name...]
                    toSend = new byte[1 + usernameBytes.Length];
                    toSend[0] = receivedData[0]; // Packet ID
                    Array.Copy(usernameBytes, 0, toSend, 1, usernameBytes.Length);
                    // Send directly back to the requester
                    _ = SendDataAsync(remoteEP, toSend);
                    break;

                // case 0x05: // Animation Packet - Add rate limiting if needed (Example commented out)
                //     // int epoch = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                //     // if (epoch - latestAnim < 0.5) break; // Example rate limit
                //     // latestAnim = epoch;
                //     goto default; // Use default broadcast logic

                default: // Handles 0x01 (Update), 0x05 (Animation), potentially others
                    // Prepare packet to broadcast: [PacketID, UserID_Hi, UserID_Lo, Original Payload...]
                    toSend = new byte[receivedData.Length + 2];
                    toSend[0] = receivedData[0]; // Packet ID
                    toSend[1] = (byte)(senderInfo.userId >> 8);
                    toSend[2] = (byte)(senderInfo.userId & 0xFF);
                    Array.Copy(receivedData, 1, toSend, 3, receivedData.Length - 1); // Copy original payload

                    // Broadcast to other clients
                    Broadcast(toSend, remoteEP);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing datagram from {remoteEP}: {ex.Message}");
            // Consider removing the client if errors persist
            // RemoveClient(remoteEP); // Example: Call RemoveClient on error
        }
    }

    // Helper to broadcast data to all clients except the sender
    private static void Broadcast(byte[] data, EndPoint senderEP)
    {
        // Create a list of keys to iterate over, avoiding modification issues
        List<EndPoint> currentClients = clients.Keys.ToList();
        foreach (EndPoint clientEP in currentClients)
        {
            // Check if the client still exists in the dictionary before sending
            if (clients.ContainsKey(clientEP) && !clientEP.Equals(senderEP)) // Don't send back to sender
            {
                _ = SendDataAsync(clientEP, data);
            }
        }
    }

    // Send data asynchronously to a specific endpoint
    private static async Task SendDataAsync(EndPoint targetEP, byte[] data)
    {
        // Add null check for serverSocket before using it
        if (serverSocket == null)
        {
             Console.WriteLine("Attempted to send data but server socket is null.");
             return;
        }
        try
        {
            // No length prefix needed for UDP
            await serverSocket.SendToAsync(data, SocketFlags.None, targetEP);
        }
        catch (SocketException se) // Catch specific socket exceptions
        {
            Console.WriteLine($"Socket error sending data to {targetEP}: {se.Message} (Code: {se.SocketErrorCode})");
            // If the error indicates the client is unreachable, consider removing them
            if (se.SocketErrorCode == SocketError.ConnectionReset ||
                se.SocketErrorCode == SocketError.ConnectionRefused || // Less common for UDP but possible
                se.SocketErrorCode == SocketError.HostUnreachable)
            {
                Console.WriteLine($"Removing client {targetEP} due to send error.");
                RemoveClient(targetEP); // Remove client if send fails indicating they are gone
            }
        }
        catch (ObjectDisposedException)
        {
             // Socket might have been closed between the null check and SendToAsync
             Console.WriteLine($"Attempted to send data on a disposed socket to {targetEP}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"General error sending data to {targetEP}: {ex.Message}");
            // Optional: Consider removing client if sending fails repeatedly
            // RemoveClient(targetEP);
        }
    }

    // Call this method when a client needs to be removed (e.g., due to timeout or error)
    private static void RemoveClient(EndPoint clientEP)
    {
        // Use TryGetValue for safer access before removing
        if (clients.TryGetValue(clientEP, out PlayerInfo? info) && info != null)
        {
            Console.WriteLine($"Client {clientEP} disconnected or removed. ID: {info.userId}");
            bool removed = clients.Remove(clientEP);
            if (!removed)
            {
                Console.WriteLine($"Warning: Failed to remove client {clientEP} from dictionary.");
                return; // Exit if removal failed
            }

            // Broadcast disconnect message
            byte[] disconnectPacket = new byte[3];
            disconnectPacket[0] = 0x02; // Disconnect Packet ID
            disconnectPacket[1] = (byte)(info.userId >> 8);
            disconnectPacket[2] = (byte)(info.userId & 0xFF);
            Broadcast(disconnectPacket, clientEP); // Inform others (pass clientEP so broadcast doesn't try sending to the removed client)
        }
        else
        {
             Console.WriteLine($"Attempted to remove non-existent or null info client: {clientEP}");
        }
    }
}

class PlayerInfo
{
    public ushort userId;
    public string username = "Unknown (s)"; // Default username

    public static List<ushort> usedIds()
    {
        List<ushort> ids = new();
        // Access Values from the static dictionary safely
        try
        {
            // Use ToList() to avoid issues if the collection is modified elsewhere
            foreach (PlayerInfo info in SocketServer.clients.Values.ToList())
            {
                // Ensure info is not null before accessing userId (though unlikely here if added correctly)
                if (info != null)
                {
                    ids.Add(info.userId);
                }
            }
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Error accessing client IDs: {ex.Message}");
        }
        return ids;
    }

    // Change return type to nullable PlayerInfo? to address CS8603
    public static PlayerInfo? FindById(ushort id)
    {
        // Access Values from the static dictionary safely
        try
        {
            // Use ToList() for safer iteration if modifications could occur
            foreach (PlayerInfo info in SocketServer.clients.Values.ToList())
            {
                 // Ensure info is not null before checking userId
                if (info != null && info.userId == id) return info;
            }
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Error finding client by ID: {ex.Message}");
        }
        // Return null if no match is found
        return null;
    }
}

#nullable disable // Optional: Restore default nullability context if needed elsewhere

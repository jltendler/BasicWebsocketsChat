using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // Map SocketID -> WebSocket
    private static ConcurrentDictionary<string, WebSocket> _sockets = new ConcurrentDictionary<string, WebSocket>();
    // Map SocketID -> Username
    private static ConcurrentDictionary<string, string> _users = new ConcurrentDictionary<string, string>();

    static async Task Main(string[] args)
    {
        var httpListener = new HttpListener();
        httpListener.Prefixes.Add("http://localhost:5000/");
        httpListener.Start();
        Console.WriteLine("WebSocket Server started at ws://localhost:5000/");

        while (true)
        {
            var context = await httpListener.GetContextAsync();
            if (context.Request.IsWebSocketRequest)
            {
                ProcessRequest(context);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            };
        };
    }

    private static async void ProcessRequest(HttpListenerContext context)
    {
        WebSocketContext? webSocketContext = null;
        string socketId = Guid.NewGuid().ToString();

        try
        {
            webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            WebSocket webSocket = webSocketContext.WebSocket;
            _sockets.TryAdd(socketId, webSocket);
            // Default username until they join
            _users.TryAdd(socketId, "DefaultUserName");

            Console.WriteLine($"Client connected: {socketId}");

            await Receive(webSocket, socketId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            if (_sockets.TryRemove(socketId, out var socket))
            {
                try
                {
                    if (socket.State != WebSocketState.Closed)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                }
                catch {}
            };

            if (_users.TryRemove(socketId, out string? username))
            {
                Console.WriteLine($"Client disconnected: {socketId} ({username})");
                if (username != "DefaultUserName" && username != null)
                {
                    await Broadcast(JsonSerializer.Serialize(new
                    {
                        type = "system",
                        subType = "error",
                        text = $"{username} disconnected."
                    }));
                    await BroadcastUserList();
                };
            };
        };
    }

    private static async Task Receive(WebSocket webSocket, string socketId)
    {
        var buffer = new byte[1024 * 4];

        while (webSocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                else
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"Received from {socketId}: {message}");

                    try
                    {
                        var jsonDoc = JsonDocument.Parse(message);
                        string? msgType = jsonDoc.RootElement.GetProperty("type").GetString();

                        if (msgType == "join")
                        {
                            string user = jsonDoc.RootElement.GetProperty("user").GetString() ?? "Guest";
                            _users[socketId] = user;
                            await Broadcast(JsonSerializer.Serialize(new
                            {
                                type = "system",
                                subType = "success",
                                text = $"{user} joined."
                            }));
                            await BroadcastUserList();
                        }
                        else if (msgType == "message")
                        {
                            string text = jsonDoc.RootElement.GetProperty("text").GetString() ?? "";
                            string user = _users.ContainsKey(socketId) ? _users[socketId] : "Unknown";
                            await Broadcast(JsonSerializer.Serialize(new
                            {
                                type = "message",
                                user = user,
                                text = text
                            }));
                        }
                        else if (msgType == "rename")
                        {
                            string oldName = _users.ContainsKey(socketId) ? _users[socketId] : "Unknown";
                            string newName = jsonDoc.RootElement.GetProperty("user").GetString() ?? oldName;

                            if (oldName != newName)
                            {
                                _users[socketId] = newName;
                                await Broadcast(JsonSerializer.Serialize(new
                                {
                                    type = "system",
                                    subType = "info",
                                    text = $"{oldName} changed name to {newName}."
                                }));
                                await BroadcastUserList();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        Console.WriteLine("Invalid JSON received");
                    }
                }
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    }

    private static async Task Broadcast(string message)
    {
        var buffer = Encoding.UTF8.GetBytes(message);
        var tasks = new List<Task>();

        foreach (var socket in _sockets.Values)
        {
            if (socket.State == WebSocketState.Open)
            {
                tasks.Add(socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None));
            }
        }

        await Task.WhenAll(tasks);
    }

    private static async Task BroadcastUserList()
    {
        // Broadcast list of users excluding the temporary defaults
        var users = _users.Values.Where(u => u != "DefaultUserName").OrderBy(u => u).ToList();
        var message = JsonSerializer.Serialize(new { type = "userlist", users = users });
        await Broadcast(message);
    }
};
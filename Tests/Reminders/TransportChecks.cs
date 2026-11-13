using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Packo.Models;
using Packo.Services;

internal static class TransportChecks
{
    public static async Task RunAsync()
    {
        var reminder = new TripReminder { Title = "Date transport", Body = "Check", Date = new DateTime(2026, 11, 13),
            RepeatUntil = new DateTime(2026, 11, 16), MinuteOfDay = 13 * 60 + 30, TimeZoneId = "Europe/Warsaw" };
        var payload = TripReminderService.ReadDefinitionPayload(JsonSerializer.Serialize(reminder));
        var requestBody = await CaptureRpcAsync(new Dictionary<string, object> { ["p_definition"] = payload });
        using var request = JsonDocument.Parse(requestBody);
        var sent = request.RootElement.GetProperty("p_definition");
        var date = sent.GetProperty("Date").GetString();
        var until = sent.GetProperty("RepeatUntil").GetString();
        Console.WriteLine($"Real Postgrest RPC wire dates: Date={date}, RepeatUntil={until}");
        if (date != "2026-11-13" || until != "2026-11-16")
            throw new Exception("The real Postgrest RPC must preserve calendar-only dates through every serialization pass.");
        Console.WriteLine("PASS The real Postgrest RPC preserves November 13 and the repeat end without UTC date shifts");
    }

    private static async Task<string> CaptureRpcAsync(Dictionary<string, object> parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var capture = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                using var headerBytes = new MemoryStream();
                var one = new byte[1];
                while (headerBytes.Length < 64 * 1024)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token);
                    headerBytes.WriteByte(one[0]);
                    if (headerBytes.Length >= 4 && Encoding.ASCII.GetString(headerBytes.GetBuffer(), (int)headerBytes.Length - 4, 4) == "\r\n\r\n") break;
                }
                var header = Encoding.ASCII.GetString(headerBytes.ToArray());
                var lengthLine = header.Split("\r\n").First(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..].Trim());
                var bodyBytes = new byte[length];
                await stream.ReadExactlyAsync(bodyBytes, timeout.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), timeout.Token);
                await stream.FlushAsync(timeout.Token);
                return Encoding.UTF8.GetString(bodyBytes);
            }, timeout.Token);
            var client = new global::Supabase.Postgrest.Client($"http://127.0.0.1:{port}");
            await client.Rpc("save_trip_reminder", parameters).WaitAsync(timeout.Token);
            return await capture;
        }
        finally { listener.Stop(); }
    }
}

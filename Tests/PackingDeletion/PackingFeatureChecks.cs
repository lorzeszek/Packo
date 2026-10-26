using Packo.Helpers;
using Packo.Models;
using Packo.Models.Enums;
using Packo.Services;
using Packo.Services.Interfaces;
using System.Text.Json;

internal static class PackingFeatureChecks
{
    public static async Task RunAsync()
    {
        var date = new DateTime(2026, 10, 26);
        var first = new PackingItem { Name = "Z", CreatedDate = date, LocalId = 1, IsPacked = true };
        var second = new PackingItem { Name = "A", CreatedDate = date, LocalId = 2 };
        var third = new PackingItem { Name = "B", CreatedDate = date.AddDays(1), LocalId = 3 };
        var items = new[] { third, first, second };
        Check(PackingItemOrder.Sort(items).SequenceEqual(new[] { second, third, first }),
            "Unpacked items come first, preserving creation order instead of alphabetical order");
        first.IsPacked = false;
        Check(PackingItemOrder.Sort(items).SequenceEqual(new[] { first, second, third }),
            "Unchecking returns an item to its original position");
        second.IsPacked = third.IsPacked = first.IsPacked = true;
        Check(PackingItemOrder.Sort(items).SequenceEqual(new[] { first, second, third }),
            "Fully packed lists and equal creation timestamps have deterministic ordering");

        first.IsPacked = second.IsPacked = third.IsPacked = false;
        var collection = new System.Collections.ObjectModel.ObservableCollection<PackingItem>(new[] { first, second, third });
        var moves = 0;
        var resets = 0;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) moves++;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };
        first.IsPacked = true;
        PackingItemOrder.Apply(collection, first);
        Check(collection.SequenceEqual(new[] { second, third, first }) && moves == 1 && resets == 0,
            "Checking moves just one row without resetting the list");
        first.IsPacked = false;
        PackingItemOrder.Apply(collection, first);
        Check(collection.SequenceEqual(new[] { first, second, third }) && moves == 2 && resets == 0,
            "Unchecking also moves just one row without resetting the list");
        var added = new PackingItem { LocalId = 4, CreatedDate = date.AddDays(2) };
        second.IsPacked = true;
        PackingItemOrder.Apply(collection, second);
        collection.Add(added);
        PackingItemOrder.Apply(collection);
        Check(collection.SequenceEqual(new[] { first, third, added, second }),
            "New unpacked items appear before packed items");

        var ai = new FakeAI { Response = "[{\"name\":\" Koszulki \",\"quantity\":6},{\"name\":\"Ładowarka\",\"quantity\":2}]" };
        var service = new PackingSuggestionService(ai);
        var result = await service.GenerateItemsAsync("Warsaw", date.AddHours(18), date.AddDays(4).AddHours(9),
            2, [3, 8], ["Car"], TripType.Leisure, "pl");
        Check(result.Count == 2 && result[0].Name == "Koszulki" && result[0].Quantity == 6 && result[1].Quantity == 2,
            "Structured AI suggestions preserve item quantities");
        Check(ai.Prompt.Contains("5 calendar days, 4 nights") && ai.Prompt.Contains("Adults: 2")
            && ai.Prompt.Contains("Age 3") && ai.Prompt.Contains("Age 8") && ai.Prompt.Contains("Polish"),
            "AI receives inclusive calendar days, nights and traveler context");
        ai.Response = "```json\n[{\"name\":\"Passport\",\"quantity\":1}]\n```";
        await service.GenerateItemsAsync("Warsaw", date, date.AddHours(23), 1, [], [], TripType.Leisure, "en");
        Check(ai.Prompt.Contains("1 calendar days, 0 nights") && ai.Prompt.Contains("English"),
            "Same-day trips use one day and fenced JSON is accepted");

        foreach (var invalid in new[] { "[]", "{}", "Socks\nCharger", "[{\"name\":\"Socks\"}]",
            "[{\"name\":\"\",\"quantity\":1}]", "[{\"name\":\"Socks\",\"quantity\":0}]",
            "[{\"name\":\"Socks\",\"quantity\":-1}]", "[{\"name\":\"Socks\",\"quantity\":1.5}]",
            "[{\"name\":\"Socks\",\"quantity\":\"2\"}]", "[{\"name\":\"Socks\",\"quantity\":2147483648}]",
            "[{\"name\":\"Socks\",\"quantity\":1},{\"name\":\"socks\",\"quantity\":2}]" })
        {
            ai.Response = invalid;
            try
            {
                await service.GenerateItemsAsync("Warsaw", date, date, 1, [], [], TripType.Leisure, "en");
                throw new Exception($"Invalid AI response was accepted: {invalid}");
            }
            catch (JsonException) { }
        }
        Check(true, "Malformed AI lists, missing/invalid quantities and duplicates are rejected");
        try
        {
            await service.GenerateItemsAsync("Warsaw", date, date.AddDays(-1), 1, [], [], TripType.Leisure, "en");
            throw new Exception("Reversed dates were accepted");
        }
        catch (ArgumentException) { }
        Check(true, "Reversed trip dates are rejected before calling AI");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class FakeAI : IAIService
    {
        public string Response { get; set; } = "";
        public string Prompt { get; private set; } = "";
        public Task<string> GetCompletionAsync(string prompt)
        {
            Prompt = prompt;
            return Task.FromResult(Response);
        }
    }
}

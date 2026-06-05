using System.Collections.ObjectModel;
using System.Text.Json;
using KickDropsMiner_WinUI.Models;
using KickDropsMiner_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KickDropsMiner_WinUI.Pages;

public sealed partial class AboutPage : Page
{
    private readonly ObservableCollection<CampaignItem> _campaigns = [];
    private readonly ObservableCollection<string> _series = [];
    private readonly DispatcherQueue _dispatcherQueue;

    public AboutPage()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        CampaignList.ItemsSource = _campaigns;
        SeriesPicker.ItemsSource = _series;
        AccountPicker.ItemsSource = AppServices.State.Accounts;
        if (AppServices.State.Accounts.Count > 0)
        {
            AccountPicker.SelectedIndex = 0;
        }
        AppServices.Bridge.EventReceived += Bridge_EventReceived;
        Unloaded += (_, _) => AppServices.Bridge.EventReceived -= Bridge_EventReceived;
        _ = LoadCachedDropsAsync();
    }

    private async void Refresh_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await LoadCachedDropsAsync();
        StatusText.Text = _campaigns.Count > 0 ? $"Showing {_campaigns.Count} cached drop(s). Updating..." : "Loading Kick drops...";
        LoadProgress.IsActive = true;
        LoadProgress.Visibility = Visibility.Visible;
        LoadBar.Value = 0;
        LoadBar.Visibility = Visibility.Visible;

        var result = await AppServices.Bridge.SendCommandAsync("fetch_drops");
        LoadProgress.IsActive = false;
        LoadProgress.Visibility = Visibility.Collapsed;
        LoadBar.Visibility = Visibility.Collapsed;
        if (!result.HasValue)
        {
            StatusText.Text = "Could not load drops.";
            return;
        }

        if (result.Value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            StatusText.Text = error.GetString() ?? "Could not load drops.";
            return;
        }

        if (!result.Value.TryGetProperty("campaigns", out var campaigns) || campaigns.ValueKind != JsonValueKind.Array)
        {
            StatusText.Text = "No campaigns found.";
            return;
        }

        if (_campaigns.Count == 0)
        {
            foreach (var campaign in campaigns.EnumerateArray())
            {
                AddOrUpdateCampaign(campaign);
                await Task.Delay(35);
            }
        }

        StatusText.Text = $"{_campaigns.Count} campaign(s) found.";
    }

    private async Task LoadCachedDropsAsync()
    {
        var cached = await AppServices.Bridge.SendCommandAsync("cached_drops");
        if (!cached.HasValue
            || !cached.Value.TryGetProperty("campaigns", out var campaigns)
            || campaigns.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _campaigns.Clear();
        foreach (var campaign in campaigns.EnumerateArray())
        {
            AddOrUpdateCampaign(campaign);
        }
    }

    private void Bridge_EventReceived(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString();
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (type == "drops_begin")
            {
                LoadBar.Value = 0;
                LoadBar.Visibility = Visibility.Visible;
                StatusText.Text = "Loading Kick drops...";
            }
            else if (type == "drops_campaign" && eventElement.TryGetProperty("campaign", out var campaign))
            {
                AddOrUpdateCampaign(campaign);
                var loaded = ReadInt(eventElement, "loaded");
                var total = Math.Max(loaded, ReadInt(eventElement, "total"));
                LoadBar.Value = total > 0 ? Math.Min(100, loaded * 100.0 / total) : 0;
                StatusText.Text = $"Loaded {loaded} of {total} drop(s)...";
            }
            else if (type == "drops_end")
            {
                LoadBar.Value = 100;
                StatusText.Text = $"{_campaigns.Count} campaign(s) found.";
            }
            else if (type == "drops_error")
            {
                StatusText.Text = ReadString(eventElement, "message");
            }
        });
    }

    private void AddOrUpdateCampaign(JsonElement campaign)
    {
        var item = ToCampaignItem(campaign);
        var existing = _campaigns.FirstOrDefault(c => c.Id == item.Id && !string.IsNullOrWhiteSpace(item.Id));
        if (existing is not null)
        {
            var index = _campaigns.IndexOf(existing);
            _campaigns[index] = item;
        }
        else
        {
            _campaigns.Add(item);
        }
        RefreshSeries();
    }

    private void CampaignList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddSelectedButton.IsEnabled = CampaignList.SelectedItems.Count > 0;
        AddSeriesButton.IsEnabled = SeriesPicker.SelectedItem is string || CampaignList.SelectedItems.Count > 0;
    }

    private void SeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddSeriesButton.IsEnabled = SeriesPicker.SelectedItem is string || CampaignList.SelectedItems.Count > 0;
    }

    private void RefreshSeries()
    {
        var selected = SeriesPicker.SelectedItem as string;
        var games = _campaigns
            .Select(c => c.Game)
            .Where(game => !string.IsNullOrWhiteSpace(game))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(game => game)
            .ToArray();

        _series.Clear();
        foreach (var game in games)
        {
            _series.Add(game);
        }

        if (!string.IsNullOrWhiteSpace(selected) && _series.Contains(selected))
        {
            SeriesPicker.SelectedItem = selected;
        }
    }

    private static CampaignItem ToCampaignItem(JsonElement campaign)
    {
        return new CampaignItem
        {
            Id = ReadString(campaign, "id"),
            Name = ReadString(campaign, "name"),
            Game = ReadString(campaign, "game"),
            Creator = ReadString(campaign, "channels"),
            Drop = NormalizeDrop(ReadString(campaign, "rewards"), ReadString(campaign, "name")),
            Time = NormalizeTime(ReadString(campaign, "time"), ReadInt(campaign, "minutes")),
            Status = ReadBool(campaign, "has_started", true) ? ReadString(campaign, "status") : "upcoming",
            Rewards = ReadString(campaign, "rewards"),
            Channels = ReadString(campaign, "channels"),
            GameImage = ReadString(campaign, "game_image"),
            RewardImage = ReadString(campaign, "reward_image"),
            RawJson = campaign.TryGetProperty("raw", out var raw) ? raw.GetRawText() : "{}"
        };
    }

    private static string NormalizeDrop(string rewards, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rewards))
        {
            return fallback;
        }

        return rewards.Replace(", ", " / ");
    }

    private static string NormalizeTime(string time, int minutes)
    {
        if (!string.IsNullOrWhiteSpace(time))
        {
            return time;
        }

        return minutes > 0 ? $"{minutes} Minutes" : "";
    }

    private async void AddCampaign_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not CampaignItem campaign)
        {
            return;
        }

        await AddCampaignsAsync(new[] { campaign }, campaign.Name);
    }

    private async void AddSelected_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var selected = CampaignList.SelectedItems.OfType<CampaignItem>().ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "Select one or more drops first.";
            return;
        }

        await AddCampaignsAsync(selected, $"{selected.Length} selected drop(s)");
    }

    private async void AddSeries_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var game = SeriesPicker.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(game))
        {
            game = CampaignList.SelectedItems.OfType<CampaignItem>().FirstOrDefault()?.Game;
        }

        if (string.IsNullOrWhiteSpace(game))
        {
            StatusText.Text = "Select a series/game or select a drop first.";
            return;
        }

        var series = _campaigns
            .Where(c => string.Equals(c.Game, game, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await AddCampaignsAsync(series, game);
    }

    private async Task AddCampaignsAsync(IReadOnlyCollection<CampaignItem> campaigns, string label)
    {
        if (campaigns.Count == 0)
        {
            StatusText.Text = "No drops found to add.";
            return;
        }

        var account = AccountPicker.SelectedItem as AccountItem;
        var documents = new List<JsonDocument>();
        try
        {
            var campaignPayload = new List<JsonElement>();
            foreach (var campaign in campaigns)
            {
                var document = JsonDocument.Parse(campaign.RawJson);
                documents.Add(document);
                campaignPayload.Add(document.RootElement.Clone());
            }

            var result = await AppServices.Bridge.SendCommandAsync("add_campaigns", new
            {
                campaigns = campaignPayload,
                account_id = account?.Id
            });

            if (!result.HasValue)
            {
                StatusText.Text = "Could not add drops.";
                return;
            }

            if (result.Value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                StatusText.Text = ReadString(result.Value, "error");
                return;
            }

            AppServices.State.ApplyBackendState(result.Value);
            var added = ReadInt(result.Value, "added");
            var skipped = ReadInt(result.Value, "skipped");
            StatusText.Text = skipped > 0
                ? $"Added {added} drop(s) from {label}; skipped {skipped}."
                : $"Added {added} drop(s) from {label}.";
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int ReadInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }

    private static bool ReadBool(JsonElement element, string property, bool defaultValue)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind == JsonValueKind.True;
    }
}

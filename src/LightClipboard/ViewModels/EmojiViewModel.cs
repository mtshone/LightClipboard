using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LightClipboard.Data;

namespace LightClipboard.ViewModels;

/// <summary>
/// Emoji 选择面板的视图模型：分类浏览 + 中英文关键字搜索。
/// </summary>
public sealed partial class EmojiViewModel : ObservableObject
{
    private const int MaxSearchResults = 300;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private EmojiCategory? _selectedCategory;

    [ObservableProperty]
    private bool _isSearching;

    public EmojiViewModel()
    {
        Categories = new ObservableCollection<EmojiCategory>(EmojiData.Categories);
        Emojis = new ObservableCollection<EmojiEntry>();
        SelectedCategory = Categories.FirstOrDefault();
        RefreshList();
    }

    public ObservableCollection<EmojiCategory> Categories { get; }

    public ObservableCollection<EmojiEntry> Emojis { get; }

    public int TotalCount => EmojiData.All.Count;

    /// <summary>用户点选了一个 Emoji。</summary>
    public event EventHandler<EmojiEntry>? Picked;

    /// <summary>搜索文本变化（由主界面的搜索框推送）。</summary>
    public void ApplySearch(string text)
    {
        SearchText = text ?? string.Empty;
    }

    public void SelectCategory(EmojiCategory? category)
    {
        SelectedCategory = category;
    }

    public void Pick(EmojiEntry entry)
    {
        if (entry != null)
        {
            Picked?.Invoke(this, entry);
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshList();

    partial void OnSelectedCategoryChanged(EmojiCategory? value)
    {
        if (!IsSearching)
        {
            RefreshList();
        }
    }

    private void RefreshList()
    {
        Emojis.Clear();

        string keyword = SearchText.Trim();
        IsSearching = keyword.Length > 0;

        IEnumerable<EmojiEntry> source;
        if (IsSearching)
        {
            source = EmojiData.All.Where(e => Matches(e, keyword));
        }
        else
        {
            source = SelectedCategory?.Emojis ?? (IEnumerable<EmojiEntry>)EmojiData.All;
        }

        foreach (var entry in source.Take(MaxSearchResults))
        {
            Emojis.Add(entry);
        }
    }

    private static bool Matches(EmojiEntry entry, string keyword)
    {
        if (entry.Value == keyword)
        {
            return true;
        }

        return entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.Keywords.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}

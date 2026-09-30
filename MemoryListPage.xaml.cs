using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>记忆面板：展示Ta记住的每一条关于用户的事实，可单独删除。</summary>
public partial class MemoryListPage : ContentPage
{
    public MemoryListPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            memoryList.ItemsSource = await ChatStore.Instance.GetMemoriesAsync();
        }
        catch { }
    }

    private async void OnDeleteMemoryClicked(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.BindingContext is not MemoryRow row) return;
            var answer = await DisplayAlert("删除记忆", $"确定忘掉这条吗？\n\n{row.Content}", "忘掉", "算了");
            if (!answer) return;

            await ChatStore.Instance.DeleteMemoryAsync(row.Id);
            // 该条记忆变化 → 让记忆注入上下文与设置页计数同步刷新
            MemoryService.NotifyMemoriesChanged();
            await LoadAsync();
        }
        catch { }
    }
}

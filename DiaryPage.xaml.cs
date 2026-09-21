using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>日记页：按日期倒序展示Ta写的每日日记；打开时顺手补写昨日日记。</summary>
public partial class DiaryPage : ContentPage
{
    public DiaryPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // 相伴天数（确保首次相遇时间已初始化）
            await ChatStore.Instance.CountMemoriesAsync();
            lblDays.Text = $"💗 已陪伴 {AppSettings.CompanionDays} 天";

            diaryList.ItemsSource = await ChatStore.Instance.GetDiariesAsync();

            // 若昨日日记还没生成，生成完刷新列表
            await DiaryService.CheckYesterdayDiaryAsync();
            diaryList.ItemsSource = await ChatStore.Instance.GetDiariesAsync();
        }
        catch { /* 加载失败保持空列表 */ }
    }
}

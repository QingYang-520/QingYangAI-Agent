using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// Ta的 头像：{avatar:"自画像描述"} 指令或设置页触发，用文生图 API 画一张
/// 并存为固定文件 avatar.png，聊天标题栏显示。生成失败静默（保持旧头像）。
/// </summary>
public static class AvatarService
{
    public static string AvatarPath => Path.Combine(FileSystem.AppDataDirectory, "avatar.png");

    public static ImageSource? Load()
    {
        try
        {
            return File.Exists(AvatarPath) ? ImageSource.FromFile(AvatarPath) : null;
        }
        catch { return null; }
    }

    /// <summary>生成并保存头像。返回是否成功（失败静默，保持旧头像）。</summary>
    public static async Task<bool> GenerateAsync(string desc)
    {
        try
        {
            if (!AppSettings.ImgEnabled || string.IsNullOrWhiteSpace(desc)) return false;

            // 走统一的生图服务：白拿"失败重试一次"和错误翻人话
            var r = await ImageGenService.GenerateAsync(
                $"为一位 AI 陪伴助手画一张头像：{desc}。要求温暖、简洁、适合做圆形头像，纯色或柔和背景。");
            if (!r.Ok || r.Bytes == null) return false;

            await File.WriteAllBytesAsync(AvatarPath, r.Bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

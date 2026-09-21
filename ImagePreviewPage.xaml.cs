using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>全屏图片鉴赏页：左上角返回 + 下载到相册。</summary>
public partial class ImagePreviewPage : ContentPage
{
    private readonly ImageSource _source;

    public ImagePreviewPage(ImageSource source)
    {
        InitializeComponent();
        _source = source;
        imgPreview.Source = source;
    }

    /// <summary>左上角返回按钮：回到聊天画面。</summary>
    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    /// <summary>下载图片到系统相册。</summary>
    private async void OnDownloadClicked(object? sender, EventArgs e)
    {
        try
        {
            // 从 ImageSource 提取二进制（支持 base64 与文件路径）
            byte[]? bytes = null;
            if (_source is StreamImageSource sis && sis.Stream != null)
            {
                using var s = await sis.Stream(default);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            else if (_source is FileImageSource fis && !string.IsNullOrEmpty(fis.File) && File.Exists(fis.File))
            {
                bytes = await File.ReadAllBytesAsync(fis.File);
            }
            else if (_source is UriImageSource uis && uis.Uri != null)
            {
                using var client = new HttpClient();
                bytes = await client.GetByteArrayAsync(uis.Uri);
            }

            if (bytes == null || bytes.Length == 0)
            {
                await DisplayAlert("下载失败", "无法读取图片数据", "确定");
                return;
            }

#if ANDROID
            // 保存到系统相册（Pictures 目录 + 媒体库扫描）
            string dir = Android.OS.Environment.GetExternalStoragePublicDirectory(
                Android.OS.Environment.DirectoryPictures)?.AbsolutePath ?? "";
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                string name = $"qingyang_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                string path = Path.Combine(dir, name);
                await File.WriteAllBytesAsync(path, bytes);

                // 通知媒体库
                var values = new Android.Content.ContentValues();
                values.Put(Android.Provider.MediaStore.Images.Media.InterfaceConsts.DisplayName, name);
                values.Put(Android.Provider.MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");
                values.Put(Android.Provider.MediaStore.Images.Media.InterfaceConsts.RelativePath,
                    Android.OS.Environment.DirectoryPictures);
                var resolver = Microsoft.Maui.ApplicationModel.Platform.AppContext.ContentResolver!;
                var uri = resolver.Insert(Android.Provider.MediaStore.Images.Media.ExternalContentUri, values);
                if (uri != null)
                {
                    using var os = resolver.OpenOutputStream(uri);
                    await os!.WriteAsync(bytes, 0, bytes.Length);
                }
                else
                {
                    // 兜底：广播扫描
                    var mediaScan = new Android.Content.Intent(Android.Content.Intent.ActionMediaScannerScanFile,
                        Android.Net.Uri.FromFile(new Java.IO.File(path)));
                    Microsoft.Maui.ApplicationModel.Platform.AppContext.SendBroadcast(mediaScan);
                }
                await DisplayAlert("已保存", $"图片已保存到相册\n{path}", "确定");
                return;
            }
#endif
            await DisplayAlert("下载失败", "无法保存到相册", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("下载失败", ex.Message, "确定");
        }
    }
}
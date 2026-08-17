using System.Windows.Media;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 「辨識過程視覺化」窗的資料：原圖標註 → 極座標字帶 → 模型輸入，加上辨識結果文字。
/// </summary>
public sealed class RecognitionProcessViewModel
{
    public RecognitionProcessViewModel(
        string fileName,
        string resultText,
        bool houghFound,
        ImageSource? originalAnnotated,
        ImageSource? polarStrip,
        ImageSource? modelInput)
    {
        FileName = fileName;
        ResultText = resultText;
        HoughFound = houghFound;
        OriginalAnnotated = originalAnnotated;
        PolarStrip = polarStrip;
        ModelInput = modelInput;
    }

    public string FileName { get; }
    public string ResultText { get; }
    public bool HoughFound { get; }

    /// <summary>① 原圖 + Hough 綠圓 + 黃方框 ROI。</summary>
    public ImageSource? OriginalAnnotated { get; }

    /// <summary>② warpPolar 展開的環狀字帶（模型實際看到的字方向）。</summary>
    public ImageSource? PolarStrip { get; }

    /// <summary>③ 白底 letterbox 後的模型輸入。</summary>
    public ImageSource? ModelInput { get; }
}

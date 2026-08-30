using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OliviaLetterOverlay;

// 全屏预览：应用背景变暗，整张信纸居中等比例放大，滚轮只滚动文字层；Esc 或右上角按钮退出。
public partial class FullLetterWindow : Window
{
    public FullLetterWindow(string reply, string date)
    {
        InitializeComponent();
        var frameWidth = Math.Min(930, SystemParameters.PrimaryScreenWidth * .82);
        LetterViewbox.Width = frameWidth;
        LetterViewbox.Height = frameWidth * 310 / 554;
        var typeface = ReplyLetterRenderer.CreateLetterTypeface(FontWeights.Normal);
        ReplyTextBlock.FontFamily = typeface.FontFamily;
        ReplyTextBlock.FontSize = 18;
        ReplyTextBlock.LineHeight = 23;
        ReplyTextBlock.Text = reply;
        DateText.Text = date.Length >= 10 ? date[..10] : date;
        DateText.FontFamily = typeface.FontFamily;
        DateText.FontSize = 14;
        PaperBackground.Source = ReplyLetterRenderer.LetterPaperSource;
        ReplyTextBlock.Padding = new Thickness(0, 0, 0, 12);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}

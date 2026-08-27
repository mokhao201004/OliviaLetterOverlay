using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class ApiSettingsWindow : Window
{
    private readonly AiProviderSettings _settings;

    public ApiSettingsWindow()
    {
        InitializeComponent();
        _settings = AiProviderStore.Load();
        ProviderCombo.SelectedIndex = _settings.Provider switch
        {
            AiProviderKind.OpenAiCompatible => 1,
            AiProviderKind.Ollama => 2,
            _ => 0,
        };
        CompatibleBaseUrlBox.Text = _settings.Provider == AiProviderKind.OpenAiCompatible ? _settings.BaseUrl : string.Empty;
        CompatibleModelBox.Text = _settings.Provider == AiProviderKind.OpenAiCompatible ? _settings.Model : string.Empty;
        OllamaModelBox.Text = _settings.Provider == AiProviderKind.Ollama ? _settings.Model : "qwen3:4b";
        AutoLetterMinutesBox.Text = AutoLetterStore.Load().IntervalMinutes.ToString();
        UpdateProviderPanels();
        Loaded += (_, _) => ProviderCombo.Focus();
    }

    private AiProviderKind SelectedProvider => ProviderCombo.SelectedIndex switch
    {
        1 => AiProviderKind.OpenAiCompatible,
        2 => AiProviderKind.Ollama,
        _ => AiProviderKind.Mimo,
    };

    private void ProviderCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderPanels();

    private void UpdateProviderPanels()
    {
        if (MimoPanel is null)
        {
            return;
        }

        MimoPanel.Visibility = SelectedProvider == AiProviderKind.Mimo ? Visibility.Visible : Visibility.Collapsed;
        CompatiblePanel.Visibility = SelectedProvider == AiProviderKind.OpenAiCompatible ? Visibility.Visible : Visibility.Collapsed;
        OllamaPanel.Visibility = SelectedProvider == AiProviderKind.Ollama ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = SelectedProvider switch
        {
            AiProviderKind.Mimo => AiProviderStore.GetMimoApiKey() is null ? "尚未配置 MiMo API Key。" : "已检测到 MiMo API Key。",
            AiProviderKind.OpenAiCompatible => AiProviderStore.GetCompatibleApiKey() is null ? "填写 URL、模型名和 API Key 后保存。" : "已检测到兼容接口 API Key；留空不会覆盖。",
            AiProviderKind.Ollama => "Ollama 不需要 API Key，但模型下载完成后需保持本地服务运行。",
            _ => string.Empty,
        };
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoLetterMinutesBox.Text.Trim(), out var intervalMinutes) || intervalMinutes < 0 || (intervalMinutes > 0 && intervalMinutes < 10))
        {
            StatusText.Text = "主动来信填 0 关闭，启用时最低为 10 分钟。";
            return;
        }

        var provider = SelectedProvider;
        var settings = new AiProviderSettings { Provider = provider };
        switch (provider)
        {
            case AiProviderKind.Mimo:
                if (!string.IsNullOrWhiteSpace(MimoApiKeyBox.Password))
                {
                    AiProviderStore.SaveMimoApiKey(MimoApiKeyBox.Password);
                }
                break;
            case AiProviderKind.OpenAiCompatible:
                settings.BaseUrl = AiProviderStore.NormalizeBaseUrl(CompatibleBaseUrlBox.Text);
                settings.Model = CompatibleModelBox.Text.Trim();
                if (!AiProviderStore.IsHttpUrl(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
                {
                    StatusText.Text = "请填写正确的 Base URL 和模型名。";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(CompatibleApiKeyBox.Password))
                {
                    AiProviderStore.SaveCompatibleApiKey(CompatibleApiKeyBox.Password);
                }
                break;
            case AiProviderKind.Ollama:
                settings.BaseUrl = AiProviderStore.DefaultOllamaBaseUrl;
                settings.Model = OllamaModelBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(settings.Model))
                {
                    StatusText.Text = "请先选择一个本地模型。";
                    return;
                }
                break;
        }

        AiProviderStore.Save(settings);
        if (!MimoClient.IsConfigured)
        {
            StatusText.Text = AiProviderStore.MissingConfigurationMessage();
            return;
        }

        var autoLetterSettings = AutoLetterStore.Load();
        autoLetterSettings.IntervalMinutes = intervalMinutes;
        AutoLetterStore.Save(autoLetterSettings);
        DialogResult = true;
        Close();
    }

    private void OpenLocalModelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var models = new LocalModelWindow(AiProviderStore.DefaultOllamaBaseUrl, OllamaModelBox.Text) { Owner = this };
        if (models.ShowDialog() != true)
        {
            return;
        }

        OllamaModelBox.Text = models.SelectedModel;
        ProviderCombo.SelectedIndex = 2;
        StatusText.Text = "已选中本地模型；点击“保存设置”后生效。";
    }

    private void OpenPersonaStudioButton_OnClick(object sender, RoutedEventArgs e)
    {
        var studio = new PersonaStudioWindow { Owner = this };
        studio.ShowDialog();
    }

    private void OpenMemoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var memory = new MemoryWindow { Owner = this };
        memory.ShowDialog();
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}

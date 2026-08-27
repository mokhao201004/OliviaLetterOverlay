using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class ApiSettingsWindow : Window
{
    private readonly AiProviderSettings _settings;
    private readonly List<string> _serviceIds = [];
    private bool _suppressSelectionChanged;

    public ApiSettingsWindow()
    {
        InitializeComponent();
        _settings = AiProviderStore.Load();

        _serviceIds.Add("mimo");
        ProviderCombo.Items.Add("MiMo 官方接口");
        foreach (var provider in CloudProviderCatalog.Providers)
        {
            _serviceIds.Add(provider.Id);
            ProviderCombo.Items.Add(provider.DisplayName);
        }

        _serviceIds.Add("ollama");
        ProviderCombo.Items.Add("本地 Ollama");

        var selectedServiceId = _settings.Provider switch
        {
            AiProviderKind.Mimo => "mimo",
            AiProviderKind.Ollama => "ollama",
            _ => _settings.CloudProviderId,
        };
        var serviceIndex = _serviceIds.FindIndex(id => string.Equals(id, selectedServiceId, StringComparison.OrdinalIgnoreCase));
        _suppressSelectionChanged = true;
        ProviderCombo.SelectedIndex = serviceIndex < 0 ? 0 : serviceIndex;
        _suppressSelectionChanged = false;

        CompatibleBaseUrlBox.Text = _settings.Provider == AiProviderKind.OpenAiCompatible ? _settings.BaseUrl : string.Empty;
        CompatibleModelBox.Text = _settings.Provider == AiProviderKind.OpenAiCompatible ? _settings.Model : string.Empty;
        CompatibleModelBox.ItemsSource = null;
        OllamaModelBox.Text = _settings.Provider == AiProviderKind.Ollama ? _settings.Model : "qwen3:4b";
        AutoLetterMinutesBox.Text = AutoLetterStore.Load().IntervalMinutes.ToString();
        UpdateProviderPanels();
        Loaded += (_, _) => ProviderCombo.Focus();
    }

    private string SelectedServiceId => ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < _serviceIds.Count
        ? _serviceIds[ProviderCombo.SelectedIndex]
        : "mimo";

    private AiProviderKind SelectedProvider => SelectedServiceId switch
    {
        "ollama" => AiProviderKind.Ollama,
        "mimo" => AiProviderKind.Mimo,
        _ => AiProviderKind.OpenAiCompatible,
    };

    private void ProviderCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        ApplySelectedCloudServicePreset();
        UpdateProviderPanels();
    }

    private void ApplySelectedCloudServicePreset()
    {
        if (SelectedProvider != AiProviderKind.OpenAiCompatible)
        {
            return;
        }

        var provider = CloudProviderCatalog.Find(SelectedServiceId);
        if (provider is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            CompatibleBaseUrlBox.Text = provider.BaseUrl;
        }

        CompatibleModelBox.Text = string.Empty;
        CompatibleModelBox.ItemsSource = null;
        StatusText.Text = $"{provider.DisplayName}：填写 API Key 后可获取模型列表。";
    }

    private async void RefreshCompatibleModelsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var baseUrl = AiProviderStore.NormalizeBaseUrl(CompatibleBaseUrlBox.Text);
        var serviceSettings = new AiProviderSettings { Provider = AiProviderKind.OpenAiCompatible, CloudProviderId = SelectedServiceId };
        var apiKey = string.IsNullOrWhiteSpace(CompatibleApiKeyBox.Password)
            ? AiProviderStore.GetCompatibleApiKey(serviceSettings)
            : CompatibleApiKeyBox.Password;

        try
        {
            RefreshCompatibleModelsButton.IsEnabled = false;
            StatusText.Text = "正在从接口获取模型列表…";
            var models = await ApiModelCatalog.ListOpenAiCompatibleModelsAsync(baseUrl, apiKey);
            CompatibleModelBox.ItemsSource = models;
            CompatibleModelBox.IsDropDownOpen = true;
            StatusText.Text = $"{CloudProviderCatalog.DisplayName(SelectedServiceId)} 返回 {models.Count} 个模型。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            RefreshCompatibleModelsButton.IsEnabled = true;
        }
    }

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
            AiProviderKind.OpenAiCompatible => CreateCompatibleStatusText(),
            AiProviderKind.Ollama => "Ollama 不需要 API Key，但模型下载完成后需保持本地服务运行。",
            _ => string.Empty,
        };
    }

    private string CreateCompatibleStatusText()
    {
        var name = CloudProviderCatalog.DisplayName(SelectedServiceId);
        return AiProviderStore.GetCompatibleApiKey(new AiProviderSettings { CloudProviderId = SelectedServiceId }) is null
            ? $"填写 {name} 的 URL、模型名和 API Key 后保存。"
            : $"已检测到 {name} API Key；留空不会覆盖。";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoLetterMinutesBox.Text.Trim(), out var intervalMinutes) || intervalMinutes < 0 || (intervalMinutes > 0 && intervalMinutes < 10))
        {
            StatusText.Text = "主动来信填 0 关闭，启用时最低为 10 分钟。";
            return;
        }

        var provider = SelectedProvider;
        var settings = new AiProviderSettings { Provider = provider, CloudProviderId = SelectedServiceId };
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
                    AiProviderStore.SaveCompatibleApiKey(settings, CompatibleApiKeyBox.Password);
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


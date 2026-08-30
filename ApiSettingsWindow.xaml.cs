using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace OliviaLetterOverlay;

public partial class ApiSettingsWindow : Window
{
    private readonly AiProviderSettings _settings;
    private readonly string _characterId = CharacterStore.Current.Id;
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
        AutoLetterMinutesBox.Text = AutoLetterStore.Load(_characterId).IntervalMinutes.ToString();
        var ttsPreferences = TtsPreferencesStore.Load();
        TtsEnabledCheck.IsChecked = ttsPreferences.Enabled;
        TtsAutoReadCheck.IsChecked = ttsPreferences.AutoReadNewLetters;
        TtsRootBox.Text = ttsPreferences.IndexTtsRoot;
        TtsExpander.IsExpanded = ttsPreferences.Enabled;
        TtsReferenceBox.Text = ttsPreferences.ReferencePath;
        TtsSeedBox.Text = ttsPreferences.Seed.ToString();
        TtsIntervalBox.Text = ttsPreferences.IntervalSilenceMs.ToString();
        TtsDurationBox.Text = ttsPreferences.DurationFactor.ToString(CultureInfo.InvariantCulture);
        TtsTokensBox.Text = ttsPreferences.MaxTextTokensPerSegment.ToString();
        UpdateTtsSetupStatus(ttsPreferences);
        var stylePreferences = StylePreferencesStore.Load();
        StyleMemoryLimitBox.Text = stylePreferences.StyleMemoryLimit.ToString();
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

    private void TtsRootBrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 IndexTTS-2.5 引擎目录（含 .venv 与 local_tools）",
        };
        if (dialog.ShowDialog(this) == true)
        {
            TtsRootBox.Text = dialog.FolderName;
        }
    }

    private void TtsSetupButton_OnClick(object sender, RoutedEventArgs e)
    {
        var detectedRoot = IndexTtsClient.FindInstalledRoot(TtsRootBox.Text.Trim());
        if (detectedRoot is not null)
        {
            TtsRootBox.Text = detectedRoot;
            TtsEnabledCheck.IsChecked = true;
            TtsExpander.IsExpanded = true;
            UpdateTtsSetupStatus(new TtsPreferences { IndexTtsRoot = detectedRoot, Enabled = true });
            StatusText.Text = "已找到 IndexTTS-2.5，保存设置后主界面会显示朗读按钮。";
            return;
        }

        const string guideUrl = "https://github.com/index-tts/index-tts";
        const string commands = "git lfs install\ngit clone https://github.com/index-tts/index-tts.git\ncd index-tts\nuv sync --all-extras\nuv tool install \"huggingface-hub[cli,hf_xet]\"\nhf download IndexTeam/IndexTTS-2.5 --local-dir=checkpoints";
        try
        {
            Clipboard.SetText(commands);
        }
        catch (Exception exception) when (exception is ExternalException or COMException)
        {
            DiagnosticLog.Write("tts", "setup_clipboard_failed");
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = guideUrl, UseShellExecute = true });
            TtsSetupStatusText.Text = "已打开官方部署页，安装命令已复制；完成后点“重新检测”。";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TtsSetupStatusText.Text = "浏览器未能自动打开；安装命令已复制，请手动打开官方部署页。";
            DiagnosticLog.Write("tts", "setup_browser_failed");
        }
    }

    private void TtsDetectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var detectedRoot = IndexTtsClient.FindInstalledRoot(TtsRootBox.Text.Trim());
        if (detectedRoot is null)
        {
            TtsSetupStatusText.Text = "未检测到完整引擎，请先部署 IndexTTS-2.5。";
            return;
        }

        TtsRootBox.Text = detectedRoot;
        TtsEnabledCheck.IsChecked = true;
        TtsExpander.IsExpanded = true;
        UpdateTtsSetupStatus(new TtsPreferences { IndexTtsRoot = detectedRoot, Enabled = true });
        StatusText.Text = "已检测到 IndexTTS-2.5，点击“保存设置”完成关联。";
    }

    private void UpdateTtsSetupStatus(TtsPreferences preferences)
    {
        TtsSetupStatusText.Text = IndexTtsClient.IsReady(preferences)
            ? "已检测到完整引擎。"
            : "未检测到完整引擎，点“一键准备”开始部署。";
    }

    private void TtsReferenceBrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择参考音色（15 秒左右的清晰人声 WAV）",
            Filter = "WAV 音频|*.wav|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            TtsReferenceBox.Text = dialog.FileName;
        }
    }

    private void ClearStyleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this, "删除当前角色已学习的全部说话风格观察？普通记忆不受影响。", "清除风格学习", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var persona = PersonaStore.Load(_characterId);
            if (persona is null)
            {
                StatusText.Text = "当前角色还没有任何学习记录。";
                return;
            }

            var kept = persona.Memories.Where(item => !item.StartsWith("用户说话：", StringComparison.Ordinal)).ToList();
            var removed = persona.Memories.Count - kept.Count;
            persona.Memories = kept;
            PersonaStore.Save(persona, _characterId);
            StatusText.Text = $"已清除 {removed} 条风格观察。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "清除失败：" + exception.Message;
        }
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

        var ttsEnabled = TtsEnabledCheck.IsChecked == true;
        var ttsRoot = TtsRootBox.Text.Trim();

        var ttsSeed = 20260830;
        if (!string.IsNullOrWhiteSpace(TtsSeedBox.Text) && (!int.TryParse(TtsSeedBox.Text.Trim(), out ttsSeed) || ttsSeed < 0))
        {
            StatusText.Text = "朗读种子需为非负整数。";
            return;
        }

        var ttsInterval = 200;
        if (!string.IsNullOrWhiteSpace(TtsIntervalBox.Text) && (!int.TryParse(TtsIntervalBox.Text.Trim(), out ttsInterval) || ttsInterval < 0))
        {
            StatusText.Text = "句间停顿需为非负整数（毫秒）。";
            return;
        }

        var ttsTokens = 120;
        if (!string.IsNullOrWhiteSpace(TtsTokensBox.Text) && (!int.TryParse(TtsTokensBox.Text.Trim(), out ttsTokens) || ttsTokens is < 1 or > 400))
        {
            StatusText.Text = "切分长度需为 1–400 的整数。";
            return;
        }

        var ttsDuration = 1.0;
        if (!string.IsNullOrWhiteSpace(TtsDurationBox.Text) && (!double.TryParse(TtsDurationBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ttsDuration) || ttsDuration is < 0.3 or > 3.0))
        {
            StatusText.Text = "语速倍率需为 0.3–3.0 之间的数字（1.0 为原生语速）。";
            return;
        }

        var ttsReference = TtsReferenceBox.Text.Trim();
        if (ttsReference.Length > 0 && !File.Exists(ttsReference))
        {
            StatusText.Text = "参考音色文件不存在；请确认路径，或清空以使用引擎自带音色。";
            return;
        }

        var ttsRootForSave = string.IsNullOrWhiteSpace(ttsRoot) ? new TtsPreferences().IndexTtsRoot : ttsRoot;
        if (ttsEnabled && !IndexTtsClient.IsReady(new TtsPreferences { IndexTtsRoot = ttsRootForSave, ReferencePath = ttsReference, Enabled = true }))
        {
            StatusText.Text = "启用了信件朗读，但没有检测到完整的 IndexTTS-2.5（需要 .venv、local_tools、checkpoints 和 reference）。";
            return;
        }

        TtsPreferencesStore.Save(new TtsPreferences
        {
            Enabled = ttsEnabled,
            IndexTtsRoot = ttsRootForSave,
            AutoReadNewLetters = TtsAutoReadCheck.IsChecked == true,
            ReferencePath = ttsReference,
            Seed = ttsSeed,
            IntervalSilenceMs = ttsInterval,
            MaxTextTokensPerSegment = ttsTokens,
            DurationFactor = ttsDuration,
        });

        var styleLimit = 5;
        if (!string.IsNullOrWhiteSpace(StyleMemoryLimitBox.Text) && (!int.TryParse(StyleMemoryLimitBox.Text.Trim(), out styleLimit) || styleLimit < 0))
        {
            StatusText.Text = "风格学习保留条数需为非负整数，填 0 表示一直保存。";
            return;
        }

        StylePreferencesStore.Save(new StylePreferences { StyleMemoryLimit = styleLimit });

        AiProviderStore.Save(settings);
        if (!MimoClient.IsConfigured)
        {
            StatusText.Text = AiProviderStore.MissingConfigurationMessage();
            return;
        }

        var autoLetterSettings = AutoLetterStore.Load(_characterId);
        autoLetterSettings.IntervalMinutes = intervalMinutes;
        AutoLetterStore.Save(autoLetterSettings, _characterId);

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

    private async void ExportLogButton_OnClick(object sender, RoutedEventArgs e) => await DiagnosticLogExport.ShowAsync(this);

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


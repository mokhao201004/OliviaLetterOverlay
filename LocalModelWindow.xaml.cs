using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class LocalModelWindow : Window
{
    public string SelectedBaseUrl { get; private set; }

    public string SelectedModel { get; private set; }

    public LocalModelWindow(string baseUrl, string currentModel)
    {
        SelectedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? AiProviderStore.DefaultOllamaBaseUrl : baseUrl;
        SelectedModel = string.IsNullOrWhiteSpace(currentModel) ? "qwen3:4b" : currentModel;
        InitializeComponent();
        DeviceRecommendationText.Text = LocalModelCatalog.GetDeviceRecommendation();
        InstallDirectoryBox.Text = OllamaPreferencesStore.Load().InstallDirectory;
        ModelPicker.ItemsSource = LocalModelCatalog.Models;
        ModelPicker.SelectedItem = LocalModelCatalog.Models.FirstOrDefault(option => option.Model == SelectedModel)
            ?? LocalModelCatalog.Models.FirstOrDefault(option => option.Model == "qwen3:4b")
            ?? LocalModelCatalog.Models[0];
        UpdateModelDetail();
        Loaded += async (_, _) => await RefreshInstalledModelsAsync();
    }

    private async Task RefreshInstalledModelsAsync()
    {
        try
        {
            StatusText.Text = "正在读取本机已安装模型…";
            var models = await OllamaClient.ListModelsAsync(SelectedBaseUrl);
            InstalledModelsBox.ItemsSource = models;
            StatusText.Text = models.Count == 0 ? "还没有安装模型。选择一个规格后点击“下载所选模型”。" : $"检测到 {models.Count} 个已安装模型。";
        }
        catch (Exception exception)
        {
            InstalledModelsBox.ItemsSource = null;
            StatusText.Text = exception.Message;
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) => await RefreshInstalledModelsAsync();

    private async void DownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ModelPicker.SelectedItem is not LocalModelOption model)
        {
            return;
        }

        try
        {
            DownloadButton.IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            await OllamaClient.PullModelAsync(SelectedBaseUrl, model.Model, progress, CancellationToken.None);
            StatusText.Text = $"{model.Title} 下载完成，可以直接使用。";
            await RefreshInstalledModelsAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private async void DownloadCustomButton_OnClick(object sender, RoutedEventArgs e)
    {
        var model = NormalizeCustomModel();
        if (string.IsNullOrEmpty(model))
        {
            return;
        }

        try
        {
            DownloadButton.IsEnabled = false;
            DownloadCustomButton.IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            await OllamaClient.PullModelAsync(SelectedBaseUrl, model, progress, CancellationToken.None);
            StatusText.Text = $"{model} 下载完成，可以直接使用。";
            SelectedModel = model;
            await RefreshInstalledModelsAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            DownloadCustomButton.IsEnabled = true;
        }
    }

    private string NormalizeCustomModel()
    {
        var model = CustomModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            StatusText.Text = "要下载预设外的模型，请先填写模型名，例如 llama3.2:3b。";
            return string.Empty;
        }

        if (model.Any(char.IsWhiteSpace) || model.Length > 200)
        {
            StatusText.Text = "模型名格式不正确。请填写 Ollama 模型名，例如 qwen3:4b。";
            return string.Empty;
        }

        return model;
    }

    private void DownloadOllamaButton_OnClick(object sender, RoutedEventArgs e)
    {
        DownloadOllamaAsync();
    }

    private async void ExportLogButton_OnClick(object sender, RoutedEventArgs e) => await DiagnosticLogExport.ShowAsync(this);

    private void StartInstaller(string installerPath)
    {
        var installDirectory = NormalizeInstallDirectory();
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            Directory.CreateDirectory(installDirectory);
            OllamaPreferencesStore.Save(new OllamaPreferences { InstallDirectory = installDirectory });
            Process.Start(new ProcessStartInfo(installerPath)
            {
                Arguments = $"/DIR=\"{installDirectory}\"",
                UseShellExecute = true,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
    }

    private string NormalizeInstallDirectory()
    {
        try
        {
            return Path.GetFullPath(InstallDirectoryBox.Text.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText.Text = "Ollama 安装目录格式不正确。";
            return string.Empty;
        }
    }

    private void BrowseInstallDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(InstallDirectoryBox.Text)
            ? InstallDirectoryBox.Text
            : AppDomain.CurrentDomain.BaseDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Ollama 安装目录",
            InitialDirectory = initialDirectory,
        };
        if (dialog.ShowDialog(this) == true)
        {
            InstallDirectoryBox.Text = dialog.FolderName;
        }
    }

    private async void DownloadOllamaAsync()
    {
        try
        {
            DownloadOllamaButton.IsEnabled = false;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var installerPath = await OllamaInstaller.DownloadAsync(progress, CancellationToken.None);
            var installNow = MessageBox.Show(
                this,
                "Ollama 官方安装包已下载完成。现在开始安装吗？",
                "安装 Ollama",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            StatusText.Text = installNow == MessageBoxResult.Yes ? "正在打开安装程序…" : "安装包已下载，需要时可再次点击“下载 Ollama”打开安装。";
            if (installNow == MessageBoxResult.Yes)
            {
                StartInstaller(installerPath);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            DownloadOllamaButton.IsEnabled = true;
        }
    }

    private void ModelPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomModelBox.Text = string.Empty;
        UpdateModelDetail();
    }

    private void InstalledModelsBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledModelsBox.SelectedItem is not string model)
        {
            return;
        }

        var knownModel = LocalModelCatalog.Models.FirstOrDefault(option => option.Model == model);
        if (knownModel is not null)
        {
            ModelPicker.SelectedItem = knownModel;
        }
        else
        {
            CustomModelBox.Text = model;
        }
    }

    private void UpdateModelDetail()
    {
        if (ModelPicker.SelectedItem is LocalModelOption model)
        {
            ModelDetailText.Text = $"{model.DownloadSize} · {model.RecommendedFor}";
        }
    }

    private void UseModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var customModel = NormalizeCustomModel();
        if (!string.IsNullOrWhiteSpace(customModel))
        {
            SelectedModel = customModel;
            DialogResult = true;
            Close();
            return;
        }

        if (ModelPicker.SelectedItem is not LocalModelOption model)
        {
            StatusText.Text = "请选择一个本地模型，或填写自定义模型名。";
            return;
        }

        SelectedModel = model.Model;
        DialogResult = true;
        Close();
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


using System.Windows;
using TapoCtrl.Services;

namespace TapoCtrl.Windows;

public partial class PythonInstallWindow : Window
{
    private readonly string _pythonPath;
    private const string InstallCommand = "python -m pip install --user --upgrade python-kasa tapo";

    public PythonInstallWindow(PythonDependencyStatus status)
    {
        InitializeComponent();
        _pythonPath = status.PythonPath;
        ReasonText.Text = status.PythonAvailable
            ? "Pythonは見つかりましたが、python-kasa または tapo が導入されていません。"
            : "Pythonを起動できません。Pythonのインストールまたは設定画面のPython実行ファイルを確認してください。";
        if (!string.IsNullOrWhiteSpace(status.Detail)) StatusText.Text = status.Detail;
        InstallButton.IsEnabled = status.PythonAvailable;
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(InstallCommand);
        StatusText.Text = "インストールコマンドをクリップボードへコピーしました。";
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = "インストール処理を実行しています。表示された画面が閉じるまでお待ちください。";
        try
        {
            var exitCode = await PythonDependencyService.InstallAsync(_pythonPath);
            var check = await PythonDependencyService.CheckAsync(_pythonPath);
            if (exitCode == 0 && check.Ready)
            {
                StatusText.Text = "インストールが完了しました。次回以降、この案内は表示されません。";
                InstallButton.Content = "完了";
                DialogResult = true;
                return;
            }
            StatusText.Text = $"インストールを確認できませんでした（終了コード {exitCode}）。コマンドをコピーしてPowerShellで実行してください。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "インストールに失敗しました: " + ex.Message;
        }
        finally
        {
            InstallButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}

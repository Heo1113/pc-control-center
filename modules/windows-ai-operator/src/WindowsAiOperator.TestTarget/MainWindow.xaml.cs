using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowsAiOperator.TestTarget;

/// <summary>
/// The real counterpart of the simulated test app (Core/Automation/Mock/MockApps.cs): same automation ids, names and
/// behaviour. Every action only changes this window's own state; nothing is written to disk or sent anywhere.
/// </summary>
public partial class MainWindow : Window
{
    private int _counter;

    public MainWindow(string? titleSuffix, bool injection)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(titleSuffix))
        {
            Title = $"Operator Test Target [{titleSuffix}]";
        }

        if (injection)
        {
            InjectionNotice.Visibility = Visibility.Visible;
        }

        for (var i = 1; i <= 50; i++)
        {
            LongList.Items.Add(new ListBoxItem { Content = $"긴 목록 {i}" });
        }
    }

    private void SetStatus(string text) => StatusText.Text = "상태: " + text;

    private void OnIncrement(object sender, RoutedEventArgs e)
    {
        _counter++;
        CounterText.Text = $"카운트: {_counter}";
        SetStatus("카운트 증가");
    }

    private void OnNotifyChanged(object sender, RoutedEventArgs e) =>
        SetStatus(NotifyCheckBox.IsChecked == true ? "알림 켜짐" : "알림 꺼짐");

    private void OnColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ColorCombo.SelectedItem is ComboBoxItem item)
        {
            SetStatus($"색상 {item.Content}");
        }
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is ListBoxItem item)
        {
            ItemList.Items.Remove(item);
            SetStatus($"{item.Content} 삭제");
        }
    }

    private void OnVanish(object sender, RoutedEventArgs e)
    {
        if (VanishingButton.Parent is Panel panel)
        {
            panel.Children.Remove(VanishingButton);
        }

        SetStatus("버튼 사라짐");
    }

    private void OnOpenDialog(object sender, RoutedEventArgs e)
    {
        SetStatus("대화상자 열림");
        var dialog = new TestDialog { Owner = this };
        var result = dialog.ShowDialog();
        SetStatus(result == true ? (dialog.OptionChecked ? "대화상자 확인(옵션 사용)" : "대화상자 확인") : "대화상자 취소");
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        NotesBox.Clear();
        SetStatus("새로 만들기");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnCloseSoon(object sender, RoutedEventArgs e)
    {
        SetStatus("1초 뒤 종료");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Application.Current.Shutdown();
        };
        timer.Start();
    }
}

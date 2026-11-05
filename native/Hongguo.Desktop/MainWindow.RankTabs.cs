using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private bool _rankGenreExpanded;

    private void UpdateRankTabs()
    {
        RankContentTabs.Children.Clear();
        foreach (RankContentOption option in _rankContents)
        {
            var button = new Button
            {
                Content = option.Name, Tag = option, Style = (Style)FindResource("ChannelButton"),
                Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(0, 0, 7, 0),
                FontSize = 14, FontWeight = FontWeights.SemiBold, Height = 38
            };
            System.Windows.Automation.AutomationProperties.SetName(button, option.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "RankContentTab:" + option.Id);
            button.Click += RankContentTab_Click;
            RankContentTabs.Children.Add(button);
        }
        RankBoardTabs.Children.Clear();
        List<RankBoardOption> boards = _rankContents.FirstOrDefault(item => item.Id == _rankContent)?.Boards ?? [];
        foreach (RankBoardOption option in boards)
        {
            var button = new Button
            {
                Content = option.Name, Tag = option, Style = (Style)FindResource("ChannelButton"),
                Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 6, 0),
                FontSize = 11, Height = 34
            };
            System.Windows.Automation.AutomationProperties.SetName(button, option.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "RankBoardTab:" + option.Id);
            button.Click += RankBoardTab_Click;
            RankBoardTabs.Children.Add(button);
        }
        UpdateRankTabSelection();
        UpdateRankCategoryPresentation();
        RevealSelectedRankTab(RankContentTabsScroll, RankContentTabs);
        RevealSelectedRankTab(RankBoardTabsScroll, RankBoardTabs);
    }

    private void UpdateRankTabSelection()
    {
        foreach (Button button in RankContentTabs.Children.OfType<Button>())
        {
            bool active = button.Tag is RankContentOption option && option.Id == _rankContent;
            button.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "WindowBrush");
            button.Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
            button.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "WindowBrush");
        }
        foreach (Button button in RankBoardTabs.Children.OfType<Button>())
        {
            bool active = !IsRankGenreMode && button.Tag is RankBoardOption option && option.Id == _rankBoard;
            button.IsEnabled = !IsRankGenreMode;
            button.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "WindowBrush");
            button.Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
            button.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "WindowBrush");
            button.ToolTip = IsRankGenreMode ? "当前按题材热度排序，点「全部题材」后可切换平台榜单。" : button.Content;
        }
    }

    private void RevealSelectedRankTab(ScrollViewer scroll, StackPanel tabs)
    {
        // 标签始终单行，选中项超出小窗口时只滚动该标签行。
        Dispatcher.BeginInvoke(() =>
        {
            Button? selected = tabs.Children.OfType<Button>().FirstOrDefault(button =>
                button.Tag is RankContentOption content ? content.Id == _rankContent
                : !IsRankGenreMode && button.Tag is RankBoardOption board && board.Id == _rankBoard);
            if (selected == null || scroll.ViewportWidth <= 0) return;
            double left = selected.TranslatePoint(new System.Windows.Point(0, 0), tabs).X;
            double right = left + selected.ActualWidth;
            if (left < scroll.HorizontalOffset) scroll.ScrollToHorizontalOffset(left);
            else if (right > scroll.HorizontalOffset + scroll.ViewportWidth)
                scroll.ScrollToHorizontalOffset(right - scroll.ViewportWidth);
        }, DispatcherPriority.Loaded);
    }

    private void RankContentTab_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as Button)?.Tag is not RankContentOption selected) return;
        if (!_rankContents.Any(item => item.Id == selected.Id) || _rankContent == selected.Id) return;
        RankContentCombo.SelectedValue = selected.Id;
    }

    private void RankBoardTab_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || IsRankGenreMode || (sender as Button)?.Tag is not RankBoardOption selected) return;
        List<RankBoardOption>? boards = _rankContents.FirstOrDefault(item => item.Id == _rankContent)?.Boards;
        if (boards == null || !boards.Any(item => item.Id == selected.Id) || _rankBoard == selected.Id) return;
        RankBoardCombo.SelectedValue = selected.Id;
    }

    private void RankTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.ScrollableWidth <= 0) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void RankCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || RankFilterGenre == null || _rankContents.Count == 0) return;
        _rankGenreExpanded = !_rankGenreExpanded;
        UpdateRankCategoryPresentation();
        if (_rankGenreExpanded) _ = RefreshRankGenreOptionsAsync();
    }

    private void UpdateRankCategoryPresentation()
    {
        bool available = RankFilterGenre != null && _rankContents.Count > 0;
        bool active = _rankGenreSelections.Count > 0;
        RankGenrePanel.Visibility = _rankGenreExpanded && available ? Visibility.Visible : Visibility.Collapsed;
        RankCategoryButton.IsEnabled = available;
        RankCategoryButton.Content = "分类" + (active ? $" · {_rankGenreSelections.Count}" : "") + (_rankGenreExpanded ? " ▴" : " ▾");
        RankCategoryButton.Background = (Brush)FindResource(active || _rankGenreExpanded ? "AccentSoftBrush" : "WindowBrush");
        RankCategoryButton.Foreground = (Brush)FindResource(active || _rankGenreExpanded ? "AccentBrush" : "MutedBrush");
        var labels = new List<string>();
        foreach (var selection in _rankGenreSelections)
        {
            string? name = _rankGenrePanel?.Rows.FirstOrDefault(row => row.Key == selection.Key)?.Options
                .FirstOrDefault(option => option.Value == selection.Value)?.Name;
            if (!string.IsNullOrWhiteSpace(name)) labels.Add(name);
        }
        string summary = string.Join(" · ", labels);
        RankCategorySummary.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RankCategorySummary.Text = "当前分类：" + (summary.Length > 0 ? summary : "已选择题材") + " · 按平台热度排序";
        RankCategoryButton.ToolTip = available
            ? active ? RankCategorySummary.Text : "按主题、人物设定与故事背景筛选热门作品"
            : "这个作品类型暂未提供题材分类，可使用下方平台榜单。";
        System.Windows.Automation.AutomationProperties.SetName(RankCategoryButton,
            (_rankGenreExpanded ? "收起" : "展开") + "排行榜题材分类" + (active ? $"，已选择 {_rankGenreSelections.Count} 项" : ""));
    }
}

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.ViewModels;

namespace SqlApp;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        this.InitializeComponent();
        PromptTextBox.TextChanged += (_, _) => ViewModel.InputText = PromptTextBox.Text;
        PipelineRange.SetRange(ViewModel.SelectedRange);
        PipelineRange.RangeChanged += ViewModel.SelectRange;

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedSession))
            {
                HookSessionMessages();
                ScrollToBottom();
                if (PromptTextBox != null)
                {
                    PromptTextBox.Text = string.Empty;
                }
            }
            else if (e.PropertyName == nameof(ViewModel.IsGenerating))
            {
                ScrollToBottom();
            }
            else if (e.PropertyName == nameof(ViewModel.InputText))
            {
                if (PromptTextBox != null && PromptTextBox.Text != ViewModel.InputText)
                {
                    PromptTextBox.Text = ViewModel.InputText;
                }
            }
            else if (e.PropertyName == nameof(ViewModel.SelectedRange))
            {
                PipelineRange.SetRange(ViewModel.SelectedRange);
            }
        };

        HookSessionMessages();
    }

    private ConversationSession? _currentHookedSession;

    private void SingleMode_Click(object sender, RoutedEventArgs e) => ViewModel.SetDualMode(false);
    private void DualMode_Click(object sender, RoutedEventArgs e) => ViewModel.SetDualMode(true);

    private void PromptSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string suggestion }) return;
        ViewModel.UsePromptSuggestionCommand.Execute(suggestion);
        PromptTextBox.Focus(FocusState.Programmatic);
        PromptTextBox.Select(PromptTextBox.Text.Length, 0);
    }

    private void HookSessionMessages()
    {
        if (_currentHookedSession != null)
        {
            _currentHookedSession.Messages.CollectionChanged -= OnMessagesChanged;
            _currentHookedSession = null;
        }

        if (ViewModel.SelectedSession != null)
        {
            _currentHookedSession = ViewModel.SelectedSession;
            _currentHookedSession.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            int total = _currentHookedSession?.Messages.Count ?? 0;
            // 새로 추가/삽입된 위치가 대화의 최신(맨 끝)인 경우에만 아래로 스크롤
            if (e.NewStartingIndex >= total - 1)
            {
                ScrollToBottom();
            }
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        DispatcherQueue?.TryEnqueue(async () =>
        {
            ChatScrollViewer?.UpdateLayout();
            if (ChatScrollViewer != null)
            {
                ChatScrollViewer.ChangeView(null, ChatScrollViewer.ExtentHeight, null, false);
            }

            // 메시지 렌더링 및 레이아웃 Measure/Arrange 완료 후 정확한 끝점으로 2차 보정
            await Task.Delay(60);
            ChatScrollViewer?.UpdateLayout();
            if (ChatScrollViewer != null)
            {
                ChatScrollViewer.ChangeView(null, ChatScrollViewer.ExtentHeight, null, false);
            }
        });
    }

    private void PromptTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter || e.OriginalKey == Windows.System.VirtualKey.Enter)
        {
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            bool isShift = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

            if (!isShift)
            {
                e.Handled = true;
                string text = PromptTextBox.Text;
                if (!string.IsNullOrWhiteSpace(text) && !ViewModel.IsGenerating)
                {
                    ViewModel.InputText = text;
                    if (ViewModel.SendMessageCommand.CanExecute(null))
                    {
                        ViewModel.SendMessageCommand.Execute(null);
                    }
                }
            }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        string text = PromptTextBox.Text;
        if (!string.IsNullOrWhiteSpace(text) && !ViewModel.IsGenerating)
        {
            ViewModel.InputText = text;
            if (ViewModel.SendMessageCommand.CanExecute(null))
            {
                ViewModel.SendMessageCommand.Execute(null);
            }
        }
    }

    private void TokensTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            if (string.IsNullOrWhiteSpace(tb.Text) || !int.TryParse(tb.Text.Trim(), out int val) || val <= 0)
            {
                tb.Text = ConversationSession.DefaultMaxTokens.ToString();
                if (ViewModel.SelectedSession != null)
                {
                    ViewModel.SelectedSession.MaxTokens = ConversationSession.DefaultMaxTokens;
                }
            }
        }
    }

    private static void InitializePickerWindow(object picker)
    {
#if WINDOWS
        if (App.AppWindow is { } window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
#endif
    }

    private async Task<string?> PickFileAsync(string filter)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(filter);

            InitializePickerWindow(picker);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"파일 선택 오류: {ex.Message}";
            return null;
        }
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync(".json");
        if (!string.IsNullOrEmpty(path))
        {
            await ViewModel.ImportJsonFileAsync(path);
        }
    }

    private void RunSqlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatSqlCandidate candidate } && ViewModel.RunCandidateCommand.CanExecute(candidate))
            ViewModel.RunCandidateCommand.Execute(candidate);
    }

    private void ToggleQueryResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ChatMessage message)
        {
            message.IsQueryResultExpanded = !message.IsQueryResultExpanded;
        }
    }

    private void AssetBundleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is AssetBundle bundle && bundle != ViewModel.CurrentSessionBundle)
        {
            ViewModel.SelectBundle(bundle);
        }
    }

    private async void BrowseAssetFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            InitializePickerWindow(picker);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) ViewModel.SelectAssetFolder(folder.Path);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"폴더 선택 오류: {ex.Message}";
        }
    }

    private void StartEditTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ConversationSession session)
        {
            ViewModel.StartEditTitle(session);
        }
    }

    private void FinishEditTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ConversationSession session)
        {
            ViewModel.FinishEditTitle(session);
        }
    }

    private void EditTitleTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter || e.OriginalKey == Windows.System.VirtualKey.Enter)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ConversationSession session)
            {
                ViewModel.FinishEditTitle(session);
                e.Handled = true;
            }
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is ConversationSession session)
        {
            ViewModel.DeleteSession(session);
        }
    }
}

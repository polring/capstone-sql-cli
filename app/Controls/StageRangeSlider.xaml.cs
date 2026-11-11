using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SqlApp.Models;
using Windows.System;

namespace SqlApp.Controls;

public sealed partial class StageRangeSlider : UserControl
{
    private static readonly string[] Labels = ["앞모델", "매퍼", "뒷모델", "후처리기"];
    private static readonly StageRange[] Choices =
    [
        new(PipelineStage.Front, PipelineStage.Front),
        new(PipelineStage.Front, PipelineStage.Mapper),
        new(PipelineStage.Front, PipelineStage.Back),
        new(PipelineStage.Front, PipelineStage.Postprocessor),
        new(PipelineStage.Back, PipelineStage.Back)
    ];
    private StageRange _range = new(PipelineStage.Front, PipelineStage.Postprocessor);
    private uint? _pointerId;
    private double _grabOffset;

    public event Action<StageRange>? RangeChanged;

    public StageRangeSlider()
    {
        InitializeComponent();
        RangeOptions.ItemsSource = Choices.Select(ChoiceLabel).ToArray();
        SetRange(_range);
    }

    public void SetRange(StageRange range)
    {
        _range = range.SnapForSelection();
        var first = (int)_range.Start;
        var last = (int)_range.End;
        TriggerTitle.Text = first == last ? $"{Labels[first]} 단독"
            : _range.End == PipelineStage.Postprocessor ? "전체 과정"
            : $"{Labels[first]} → {Labels[last]}";
        AutomationProperties.SetName(RangeButton, $"실행 구간 선택, {ChoiceLabel(_range)}");
        RangeOptions.SelectedIndex = Array.IndexOf(Choices, _range);
        UpdateTrack();
    }

    private static string ChoiceLabel(StageRange range)
    {
        int first = (int)range.Start;
        int last = (int)range.End;
        return first == last ? $"{Labels[first]} 단독" : string.Join(" → ", Labels, first, last - first + 1);
    }

    private void Flyout_Opened(object sender, object e)
    {
        UpdateTrack();
    }

    private void Flyout_Closed(object sender, object e) => ShowSlider();

    private void ShowChoices_Click(object sender, RoutedEventArgs e)
    {
        RangeOptions.Visibility = Visibility.Visible;
        SliderView.Visibility = Visibility.Collapsed;
        RangeOptions.UpdateLayout();
        var selected = RangeOptions.ContainerFromIndex(RangeOptions.SelectedIndex) as ListViewItem;
        ((Control?)selected ?? RangeOptions).Focus(FocusState.Programmatic);
    }

    private void ShowSlider()
    {
        SliderView.Visibility = Visibility.Visible;
        RangeOptions.Visibility = Visibility.Collapsed;
    }

    private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((ListView)sender).SelectedIndex;
        if (index >= 0 && Choices[index] != _range && RangeOptions.Visibility == Visibility.Visible)
            ConfirmChoice(Choices[index]);
    }

    private void Option_Click(object sender, ItemClickEventArgs e)
    {
        int index = RangeOptions.Items.IndexOf(e.ClickedItem);
        if (index >= 0 && RangeOptions.Visibility == Visibility.Visible)
            ConfirmChoice(Choices[index]);
    }

    private void ConfirmChoice(StageRange choice)
    {
        SelectRange(choice);
        ShowSlider();
        ShowChoicesButton.Focus(FocusState.Programmatic);
    }

    private void SelectRange(StageRange range)
    {
        if (_range == range) return;
        SetRange(range);
        RangeChanged?.Invoke(_range);
    }

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrack();

    private void UpdateTrack()
    {
        if (EndHandle is null) return;
        double cellWidth = Track.ActualWidth / Labels.Length;
        int first = (int)_range.Start;
        int last = (int)_range.End;
        Canvas.SetLeft(StartHandle, first * cellWidth);
        Canvas.SetLeft(EndHandle, (last + 1) * cellWidth);
        Canvas.SetLeft(SelectedBand, first * cellWidth);
        SelectedBand.Width = (last - first + 1) * cellWidth;
        AutomationProperties.SetName(StartHandle, $"시작 단계, {Labels[first]}");
        AutomationProperties.SetName(EndHandle, $"종료 단계, {Labels[last]}");
    }

    private int BoundaryOf(ContentControl handle) => handle == StartHandle ? (int)_range.Start : (int)_range.End + 1;

    private void MoveBoundary(ContentControl handle, int boundary)
    {
        var range = handle == StartHandle
            ? _range.WithStart((PipelineStage)Math.Clamp(boundary, 0, Labels.Length - 1))
            : _range.WithEnd((PipelineStage)Math.Clamp(boundary - 1, 0, Labels.Length - 1));
        SelectRange(range);
    }

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (ContentControl)sender;
        var point = e.GetCurrentPoint(Track);
        if (_pointerId.HasValue || !point.Properties.IsLeftButtonPressed || !handle.CapturePointer(e.Pointer)) return;
        _pointerId = e.Pointer.PointerId;
        _grabOffset = point.Position.X - BoundaryOf(handle) * Track.ActualWidth / Labels.Length;
        handle.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId || Track.ActualWidth <= 0) return;
        double x = e.GetCurrentPoint(Track).Position.X - _grabOffset;
        MoveBoundary((ContentControl)sender, (int)Math.Round(x * Labels.Length / Track.ActualWidth));
        e.Handled = true;
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        _pointerId = null;
        ((ContentControl)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Handle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId) _pointerId = null;
    }

    private void Handle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        var handle = (ContentControl)sender;
        int boundary = handle == StartHandle
            ? (int)(e.Key == VirtualKey.Left ? PipelineStage.Front : PipelineStage.Back)
            : BoundaryOf(handle) + (e.Key == VirtualKey.Left ? -1 : 1);
        MoveBoundary(handle, boundary);
        e.Handled = true;
    }
}

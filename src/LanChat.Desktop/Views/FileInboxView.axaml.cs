using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Desktop.ViewModels;

namespace LanChat.Desktop.Views;

public partial class FileInboxView : UserControl
{
    private FileInboxViewModel? _model;
    private bool _followTail = true;
    private bool _scrollQueued;
    private bool _preserving;
    private Border? _dropTarget;

    public FileInboxView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) { _model.ScrollRequested -= ScrollToEnd; _model.PropertyChanged -= ModelPropertyChanged; }
            _model = DataContext as FileInboxViewModel;
            if (_model is not null) { _model.ScrollRequested += ScrollToEnd; _model.PropertyChanged += ModelPropertyChanged; }
        };
        FileScroll.ScrollChanged += async (_, args) =>
        {
            if (_preserving) return;
            if (args.ExtentDelta != default || args.ViewportDelta != default) ScrollToEnd(false);
            else if (args.OffsetDelta != default) _followTail = IsNearBottom();
            if (args.OffsetDelta.Y < 0 && FileScroll.Offset.Y < 80) await LoadOlderAsync();
        };
        FileScroll.AddHandler(PointerWheelChangedEvent, async (_, args) =>
        {
            if (args.Delta.Y <= 0) return;
            _followTail = false;
            if (FileScroll.Offset.Y < 80) await LoadOlderAsync();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty && IsVisible) ScrollToEnd(false); };
        AddHandler(DragDrop.DragOverEvent, FilesDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragEnterEvent, FilesDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropTarget(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, FilesDropped, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    private void ModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FileInboxViewModel.SelectedTab)) { ClearDropTarget(); if (_model?.IsHistory == true) ScrollToEnd(false); }
        if (args.PropertyName != nameof(FileInboxViewModel.IsPickerOpen)) return;
        Dispatcher.UIThread.Post(() => { if (_model?.IsPickerOpen == true) CancelRecipientButton.Focus(); else SendFilesButton.Focus(); });
    }

    private bool IsNearBottom() => FileScroll.Extent.Height - FileScroll.Viewport.Height - FileScroll.Offset.Y < 60;
    private void ScrollToEnd(bool force)
    {
        if (force) _followTail = true;
        if (!_followTail || _scrollQueued || _preserving) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!_followTail || _preserving || !IsEffectivelyVisible || _model?.IsHistory != true) return;
            FileScroll.UpdateLayout();
            if (FileItems.ItemCount > 0) FileItems.ScrollIntoView(FileItems.ItemCount - 1);
            FileScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    public async Task LoadOlderAsync()
    {
        if (_preserving || _model is not { IsLoading: false, HasMore: true } model) return;
        var anchor = FileItems.GetRealizedContainers().FirstOrDefault(control =>
        {
            var point = control.TranslatePoint(default, FileScroll);
            return point.HasValue && point.Value.Y + control.Bounds.Height > 0;
        });
        var item = anchor?.DataContext as FileHistoryItemViewModel;
        var position = anchor?.TranslatePoint(default, FileScroll)?.Y ?? 0;
        _preserving = true; _followTail = false;
        try
        {
            if (!await model.LoadOlderAsync() || item is null || !model.Files.Contains(item)) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FileItems.ScrollIntoView(model.Files.IndexOf(item));
                FileScroll.UpdateLayout();
                var container = FileItems.ContainerFromItem(item);
                if (container?.TranslatePoint(default, FileScroll) is { } point)
                    FileScroll.Offset = new Vector(FileScroll.Offset.X, FileScroll.Offset.Y + point.Y - position);
            }, DispatcherPriority.Background);
        }
        finally { _preserving = false; }
    }

    private void FilesDragOver(object? sender, DragEventArgs args)
    {
        if (!args.DataTransfer.Contains(DataFormat.File)) return;
        var ready = _model is { IsSending: false, IsPickerOpen: false } && (args.DragEffects & DragDropEffects.Copy) != 0;
        args.DragEffects = ready ? DragDropEffects.Copy : DragDropEffects.None;
        var target = ready ? FindDeviceRow(args) : null;
        if (_dropTarget != target) { _dropTarget?.Classes.Remove("drop-target"); _dropTarget = target; _dropTarget?.Classes.Add("drop-target"); }
        DropOverlay.IsVisible = ready && target is null;
        FileDropHint.Text = target?.DataContext is PeerViewModel peer ? "松开发送给 " + peer.Nickname : "拖文件到在线设备即可直接发送";
        args.Handled = true;
    }

    private Border? FindDeviceRow(DragEventArgs args) => _model?.IsDevices == true && args.Source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(x => x.Classes.Contains("deviceRow") && x.DataContext is PeerViewModel { IsOnline: true } && x.FindAncestorOfType<ItemsControl>() == OnlineDeviceList) : null;

    private void ClearDropTarget()
    {
        _dropTarget?.Classes.Remove("drop-target"); _dropTarget = null; DropOverlay.IsVisible = false;
        FileDropHint.Text = "拖文件到在线设备即可直接发送";
    }

    private async void FilesDropped(object? sender, DragEventArgs args)
    {
        var recipient = FindDeviceRow(args)?.DataContext as PeerViewModel;
        ClearDropTarget();
        if (!args.DataTransfer.Contains(DataFormat.File)) return;
        args.Handled = true;
        if (_model is not { IsSending: false, IsPickerOpen: false } || (args.DragEffects & DragDropEffects.Copy) == 0) { args.DragEffects = DragDropEffects.None; return; }
        var paths = args.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        args.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (paths.Length == 0) return;
        if (recipient is not null) await _model.SendDroppedFilesToDeviceAsync(recipient, paths);
        else _model.BeginSendFiles(paths);
    }

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (_model is null) return;
        if (args.Key == Key.Escape)
        {
            if (_model.IsPickerOpen) { _model.CancelPicker(); args.Handled = true; }
            else if (FileSearch.IsFocused && _model.HasSearch) { _model.ClearSearchCommand.Execute(null); args.Handled = true; }
        }
        else if (_model.IsPickerOpen && args.Key == Key.Enter && RecipientList.IsKeyboardFocusWithin && _model.CanConfirm)
        { _model.ConfirmSendCommand.Execute(null); args.Handled = true; }
    }
}

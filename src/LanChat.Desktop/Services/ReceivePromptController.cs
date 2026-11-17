using System.ComponentModel;
using Avalonia.Controls;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;

namespace LanChat.Desktop.Services;

/// <summary>Displays queued receive confirmations without changing the main window or current page.</summary>
public sealed class ReceivePromptController : IDisposable
{
    private readonly Window _mainWindow;
    private readonly Window _prompt;
    private readonly ReceiveOffersViewModel _offers;
    private readonly UserProfile _profile;
    private bool _disposed;

    public ReceivePromptController(Window mainWindow, Window prompt, ReceiveOffersViewModel offers, UserProfile profile)
    {
        _mainWindow = mainWindow; _prompt = prompt; _offers = offers; _profile = profile;
        offers.PropertyChanged += OnOfferChanged;
        offers.OfferQueued += OnOfferQueued;
        mainWindow.Activated += OnMainWindowActivated;
    }

    private void OnOfferChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(ReceiveOffersViewModel.Current)) return;
        if (!_offers.IsOpen) { _prompt.Hide(); return; }
        ShowIfNotificationsEnabled();
    }

    private void OnOfferQueued() => ShowIfNotificationsEnabled();

    private void ShowIfNotificationsEnabled()
    {
        var background = !_mainWindow.IsVisible || !_mainWindow.IsActive || _mainWindow.WindowState == WindowState.Minimized;
        if (!background || _profile.ShowNotifications) ShowPending();
    }

    private void OnMainWindowActivated(object? sender, EventArgs args) => ShowPending();

    private void ShowPending()
    {
        if (_disposed || !_offers.IsOpen) return;
        if (!_prompt.IsVisible) _prompt.Show();
        _prompt.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _offers.PropertyChanged -= OnOfferChanged;
        _offers.OfferQueued -= OnOfferQueued;
        _mainWindow.Activated -= OnMainWindowActivated;
        _prompt.Hide();
    }
}

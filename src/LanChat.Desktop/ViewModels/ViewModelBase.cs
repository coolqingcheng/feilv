using CommunityToolkit.Mvvm.ComponentModel;

namespace LanChat.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
}

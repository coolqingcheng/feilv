using CommunityToolkit.Mvvm.ComponentModel;

namespace LanChat.Desktop.ViewModels;

public partial class UpdateProgressViewModel : ViewModelBase
{
    [ObservableProperty] private string _status = "正在准备更新…";
}

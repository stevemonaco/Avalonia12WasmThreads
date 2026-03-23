using CommunityToolkit.Mvvm.ComponentModel;

namespace Avalonia12WasmThreads.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Avalonia!";
}

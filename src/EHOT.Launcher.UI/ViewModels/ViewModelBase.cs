using CommunityToolkit.Mvvm.ComponentModel;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>База для всех ViewModel: состояния загрузки/ошибки и уведомление об изменении.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isLoaded;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Вызывается при первом показе страницы (ленивая инициализация тяжёлых данных).</summary>
    public virtual Task OnNavigatedToAsync(CancellationToken ct = default) => Task.CompletedTask;

    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}

/// <summary>Страница, которой нужны параметры навигации.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter, CancellationToken ct = default);
}

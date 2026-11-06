using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopHub.App.Main;

/// <summary>Base mínima de INotifyPropertyChanged (evita dependência de um toolkit MVVM inteiro).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="propertyName">null ou vazio notifica todas as propriedades.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

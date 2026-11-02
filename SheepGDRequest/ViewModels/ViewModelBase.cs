using CommunityToolkit.Mvvm.ComponentModel;

namespace SheepGDRequest.ViewModels;

/// <summary>
/// Base de los ViewModels de la aplicación (MainWindowViewModel y SettingsViewModel). Hereda de
/// ObservableObject, que implementa INotifyPropertyChanged para que la interfaz se actualice
/// cuando cambian sus propiedades. ViewLocator la usa para reconocer qué objetos son ViewModels.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}

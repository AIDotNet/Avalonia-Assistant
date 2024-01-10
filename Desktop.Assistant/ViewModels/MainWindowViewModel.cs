using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Desktop.Assistant.Services;
using Desktop.Assistant.Domain.Models;
using ReactiveUI;

namespace Desktop.Assistant.ViewModels
{
    public class MainWindowViewModel : ReactiveObject, IScreen
    {
        // The Router associated with this Screen.
        // Required by the IScreen interface.
        public RoutingState Router { get; }
              
   
        public MainWindowViewModel()
        {
            
            Router = new RoutingState();
            Router.Navigate.Execute(new MainViewModel(Router));
        }

    }
}

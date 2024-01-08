using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Assistant.Domain.Models;
using Desktop.Assistant.Services;
using ReactiveUI;

namespace Desktop.Assistant.ViewModels
{
    public class WelcomeViewModel : ViewModelBase
    {
        public string Endpoint
        {
            get => endpoint;
            set => this.RaiseAndSetIfChanged(ref endpoint, value);
        }

        public string Key
        {
            get => key;
            set => this.RaiseAndSetIfChanged(ref key, value);
        }

        public string Model
        {
            get => model;
            set => this.RaiseAndSetIfChanged(ref model, value);
        }


        public ICommand CompleteCommand { get; private set; }

      

        public WelcomeViewModel(RoutingState router) : base(router)
        {

            CompleteCommand = ReactiveCommand.Create(Complete);
        }

        public void Complete()
        {
            try
            {
              
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        //Fields
        private ChatService chatService;
        private string key;
        private string model;
        private string endpoint;
    }
}

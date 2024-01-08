using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Assistant.Services;
using Microsoft.AspNetCore.SignalR.Client;
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

     

        public bool Connected
        {
            get => connected;
            set => this.RaiseAndSetIfChanged(ref connected, value);
        }

        public WelcomeViewModel(RoutingState router) : base(router)
        {
            Endpoint = "https://bellowserver.azurewebsites.net/";
         
        }

        //Fields
        private ChatService chatService;
        private string key;
        private string model;
        private string endpoint;
        private bool connected;
    }
}

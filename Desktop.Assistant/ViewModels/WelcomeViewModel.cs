using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Metadata;
using Desktop.Assistant.Domain.Model;
using Desktop.Assistant.Domain.Models;
using Desktop.Assistant.Services;
using Desktop.Assistant.Views;
using Newtonsoft.Json;
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
            if (File.Exists(file))
            {
                var lines = File.ReadAllText(file);
                var openai = JsonConvert.DeserializeObject<OpenAIModel>(lines);
                Endpoint = openai.EndPoint;
                Key = openai.Key;
                Model = openai.Model;
            }
         
            CompleteCommand = ReactiveCommand.Create(Complete);
        }

        public void Complete()
        {
            try
            {
                OpenAIModel openAIModel = new OpenAIModel();
                openAIModel.EndPoint = Endpoint;
                openAIModel.Key = Key;
                openAIModel.Model = Model;
                var lines = JsonConvert.SerializeObject(openAIModel);
                File.WriteAllText(file, lines);

                Router.Navigate.Execute(new ChatViewModel(chatService, Router));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        //Fields
        private string file = "openai.json";
        private ChatService chatService = new ChatService();
        private string key;
        private string model;
        private string endpoint;
    }
}

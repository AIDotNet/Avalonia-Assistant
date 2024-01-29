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
        public string GgufPath
        {
            get => ggufpath;
            set => this.RaiseAndSetIfChanged(ref ggufpath, value);
        }


        //公开属性
        public bool IsCheckOpenAI
        {
            get => isCheckOpenAI;
            set => this.RaiseAndSetIfChanged(ref isCheckOpenAI, value);
        }

        public bool IsCheckgguf
        {
            get => isCheckgguf;
            set => this.RaiseAndSetIfChanged(ref isCheckgguf, value);
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
            GgufPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tinyllama-1.1b-chat.gguf");
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
                //存储变量后面使用
                OpenAIOption.EndPoint = Endpoint;
                OpenAIOption.Key = Key;
                OpenAIOption.Model = Model;

                Router.Navigate.Execute(new ChatViewModel( Router, IsCheckOpenAI,this.GgufPath));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        //Fields
        private string file = "openai.json";
        private string key;
        private string model;
        private string endpoint;
        private bool isCheckOpenAI = true;
        private bool isCheckgguf = false;

        private string ggufpath= "tinyllama-1.1b-chat.gguf";
    }
}

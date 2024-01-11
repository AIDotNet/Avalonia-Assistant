using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Assistant.Services;
using Desktop.Assistant.Models;
using Avalonia.Controls.Notifications;
using System.Reactive.Linq;
using Desktop.Assistant.Domain.Model;
using Microsoft.SemanticKernel;
using System.Net.Http;
using Desktop.Assistant.Domain.Utils;
using System.Data;
using Microsoft.SemanticKernel.Planning.Handlebars;
using Desktop.Assistant.Domain.NativePlugins.Attributes;
using System.IO;
using Whisper.net;
using System.Diagnostics;
using Avalonia;
using Splat;

namespace Desktop.Assistant.ViewModels
{
    public class ChatViewModel : ViewModelBase
    {
        public ObservableCollection<MessageBase> Messages { get; private set; }
        private Kernel kernel;
        //录音相关
        private bool _isRecording = false;
        private AudioRecorder audioRecorder;
        private WhisperProcessor processor;

        public string NewMessageContent
        {
            get => newMessageContent;
            set => this.RaiseAndSetIfChanged(ref newMessageContent, value);
        }
        
        public  ICommand DictateMessageCommand { get; private set; }

        public ICommand SendMessageCommand { get; private set; }

        public ICommand EnterKeyPressedCommand { get; private set; }

        public ChatViewModel( RoutingState router) : base(router)
        {
            this.Messages = new ObservableCollection<MessageBase>();
 

            canSendMessage = this.WhenAnyValue(x => x.NewMessageContent).Select(x => !string.IsNullOrEmpty(x));

            SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessage, canSendMessage);
            DictateMessageCommand = ReactiveCommand.CreateFromTask(DictateMessage);
            EnterKeyPressedCommand = ReactiveCommand.CreateFromTask(EnterKeyPressed);

            //实例化SK
            var handler = new OpenAIHttpClientHandler();
            kernel = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(
                  modelId: OpenAIOption.Model,
            apiKey: OpenAIOption.Key,
                  httpClient: new HttpClient(handler)
                     )
                .Build();
            //注入SK插件
            OSExtensions.ImportPluginFromObjectByOs(kernel);

            //录音
            processor = Locator.Current.GetService<WhisperProcessor>();
            audioRecorder = new AudioRecorder();
        }

        /// <summary>
        /// 回车
        /// </summary>
        /// <returns></returns>
        async Task EnterKeyPressed()
        {
            await SendMessage();
        }

        /// <summary>
        /// 按钮发送消息
        /// </summary>
        /// <returns></returns>
        async Task SendMessage()
        {
            string outMsg = "";
            try
            {
                string inputMsg = NewMessageContent;
                NewMessageContent = string.Empty;
                this.Messages.Add(new TextMessage(inputMsg) { Role = ChatRoleType.Sender });
                //OpenAIChatCompletionService chatCompletionService = new(OpenAIOption.Model, OpenAIOption.Key, httpClient: new HttpClient(handler));
                //var msg=await chatCompletionService.GetChatMessageContentAsync(NewMessageContent); 
                var planner = new HandlebarsPlanner(
                   new HandlebarsPlannerOptions()
                   {
                       AllowLoops = true
                   });
                var plan = await planner.CreatePlanAsync(kernel, inputMsg);
                outMsg = await plan.InvokeAsync(kernel);           
            }
            catch (Exception ex)
            {
                outMsg = "执行异常";
            }
  
            this.Messages.Add(new TextMessage(outMsg) { Role = ChatRoleType.Receiver });
        }

        async Task DictateMessage()
        {
            try
            {
                var outputFolder = Path.Combine(AppContext.BaseDirectory, "NAudio");
                Directory.CreateDirectory(outputFolder);
                var outputFilePath = Path.Combine(outputFolder, "recorded.wav");
                // 检查是否正在录音
                if (!_isRecording)
                {
                    audioRecorder.StartRecording(outputFilePath);
                }
                else
                {
                    audioRecorder.StopRecording();
                    await Task.Delay(500);
                    //结束后解析文字
                    
                    var audioStr = string.Empty;
                    using (var fileStream = File.OpenRead(outputFilePath))
                    {
                        await foreach (var result in processor.ProcessAsync(fileStream))
                        {
                            audioStr += result.Text;
                        }
                    }
                    NewMessageContent = audioStr;
                    await SendMessage();
                }
                _isRecording = !_isRecording;
            }
            catch (Exception ex)
            { 
                
            }
        }


        //Fields
        private string newMessageContent;
        private WindowNotificationManager windowNotificationManager;
        private IObservable<bool> canSendMessage;
    }
}

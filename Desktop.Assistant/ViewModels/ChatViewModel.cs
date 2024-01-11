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
using Desktop.Assistant.Utils;
using Avalonia.Media.Imaging;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Plugins.Core;
using System.Text;

namespace Desktop.Assistant.ViewModels
{
    public class ChatViewModel : ViewModelBase
    {
        private Kernel kernel;
        //录音相关
        private bool _isRecording = false;
        private AudioRecorder audioRecorder;
        private WhisperProcessor processor;
        private OpenAIChatCompletionService chatCompletionService;
        //Fields
        private string newMessageContent;
        private Bitmap? micImageBinding = ImageHelper.LoadFromResource(new Uri("avares://Desktop.Assistant/Assets/mic.png"));
        private IObservable<bool> canSendMessage;
        private bool? isCheckGPT = true;
        private bool? isCheckAgent = false;

        //公开属性
        public bool? IsCheckGPT 
        {
            get => isCheckGPT;
            set => this.RaiseAndSetIfChanged(ref isCheckGPT, value);
        }

        public bool? IsCheckAgent
        {
            get => isCheckAgent;
            set => this.RaiseAndSetIfChanged(ref isCheckAgent, value);
        }

        public ObservableCollection<MessageBase> Messages { get; private set; }
        public Bitmap? MicImageBinding
        {
            get => micImageBinding;
            set => this.RaiseAndSetIfChanged(ref micImageBinding, value);
        }

        public string NewMessageContent
        {
            get => newMessageContent;
            set => this.RaiseAndSetIfChanged(ref newMessageContent, value);
        }
        

        //Commands
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
            kernel.ImportPluginFromObject(new ConversationSummaryPlugin(), "ConversationSummaryPlugin");
            OSExtensions.ImportPluginFromObjectByOs(kernel);
            chatCompletionService = new(OpenAIOption.Model, OpenAIOption.Key, httpClient: new HttpClient(handler));
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
            string inputMsg = NewMessageContent;
            NewMessageContent = string.Empty;
            this.Messages.Add(new TextMessage(inputMsg) { Role = ChatRoleType.Sender });
            string outMsg = "";
            //判断是GPT还是Agent
            if (IsCheckGPT==true)
            {
                outMsg= await ChatGPT(inputMsg);
            }
            else {
                outMsg= await AIAgent(inputMsg);
            }
            this.Messages.Add(new TextMessage(outMsg) { Role = ChatRoleType.Receiver });
        }
        private async Task<string> ChatGPT(string inputMsg)
        {
            string outMsg = "";
            try
            {
                StringBuilder hisList=new StringBuilder();
                string hisMsg = "";
                if (Messages.Count > 1)
                {
                    foreach (var msg in Messages)
                    {
                        string role = msg.Role == ChatRoleType.Sender ? "user" : "assistant";
                        hisList.AppendLine($"{role}:{((TextMessage)msg).Content}");
                    }
                    KernelFunction SummarizeConversationFunction = kernel.Plugins.GetFunction("ConversationSummaryPlugin", "SummarizeConversation");
                    hisMsg = (await kernel.InvokeAsync(SummarizeConversationFunction, new KernelArguments() { ["input"] = $"内容是：{hisList.ToString()} \r\n 请注意用中文总结。" })).GetValue<string>();
                }               
                string  prompt = @$"历史会话概要:{hisMsg}\n
                                   用户：{inputMsg}";
                var result= await chatCompletionService.GetChatMessageContentAsync(prompt);
                if (result != null)
                {
                    outMsg = result.Content;
                }
            }
            catch (Exception ex)
            {
                outMsg = $"执行异常:{ex.Message}";
            }
            return outMsg;
        }

        private async Task<string> AIAgent(string inputMsg)
        {
            string outMsg = "";
            try
            {       
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
                outMsg = $"执行异常:{ex.Message}";
            }
            return outMsg;        
        }

        async Task DictateMessage()
        {
            try
            {
                var outputFolder = Path.Combine(AppContext.BaseDirectory, "NAudio");
                Directory.CreateDirectory(outputFolder);
                var outputFilePath = Path.Combine(outputFolder, "recorded.wav");
                _isRecording = !_isRecording;
                //改变mic图标
                await ChangeMicImage();
                // 检查是否正在录音
                if (_isRecording)
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
            }
            catch (Exception ex)
            { 
                
            }
        }

        /// <summary>
        /// 改变图标颜色
        /// </summary>
        /// <returns></returns>
        private async Task ChangeMicImage()
        {
            if (_isRecording)
            {
                MicImageBinding = ImageHelper.LoadFromResource(new Uri("avares://Desktop.Assistant/Assets/mic-red.png"));
            }
            else
            {
                MicImageBinding = ImageHelper.LoadFromResource(new Uri("avares://Desktop.Assistant/Assets/mic.png"));
            }
        }
    }
}

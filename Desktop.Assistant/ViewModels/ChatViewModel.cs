using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Assistant.Services;
using Desktop.Assistant.Domain.MessageTypes;
using Desktop.Assistant.Domain.Models;
using Desktop.Assistant.Models;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using System.Reactive.Linq;
using Desktop.Assistant.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.Net.Http;
using Desktop.Assistant.Domain.Utils;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Desktop.Assistant.ViewModels
{
    public class ChatViewModel : ViewModelBase
    {        
        public ObservableCollection<MessageBase> Messages { get; private set; }

        public string NewMessageContent
        {
            get => newMessageContent;
            set => this.RaiseAndSetIfChanged(ref newMessageContent, value);
        }
        
        public  ICommand DictateMessageCommand { get; private set; }

        public ICommand AttachImageCommand { get; private set; }

        public ICommand SendMessageCommand { get; private set; }

        public ChatViewModel(ChatService chatService, RoutingState router) : base(router)
        {
            this.Messages = new ObservableCollection<MessageBase>();
            this.chatService = chatService;
            this.chatService.Messages.CollectionChanged += (sender, args) =>
            {                
                foreach (MessagePayload newMsg in args.NewItems)
                {
                    ChatRoleType role = ChatRoleType.Receiver;
                    if (newMsg.AuthorUsername == chatService.CurrentUser.UserName)
                        role = ChatRoleType.Sender;

                    switch (newMsg.Type)
                    {
                        case MessageType.Text:
                            Messages.Add(new TextMessage(newMsg) { Role = role });
                            break;
                        case MessageType.Link:
                            Messages.Add(new LinkMessage(newMsg) { Role = role });
                            break;
                        case MessageType.Image:
                            Messages.Add(new ImageMessage(newMsg) { Role = role });
                            break;
                    }
                }
            };

            this.chatService.ParticipantLoggedIn.Subscribe(x => { Messages.Add(new UserConnectedMessage(x)); });
            this.chatService.ParticipantLoggedOut.Subscribe(x => { Messages.Add(new UserDisconnectedMessage(x)); });

            canSendMessage = this.WhenAnyValue(x => x.NewMessageContent).Select(x => !string.IsNullOrEmpty(x));

            SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessage, canSendMessage);
            AttachImageCommand = ReactiveCommand.CreateFromTask(AttachImage);
            DictateMessageCommand = ReactiveCommand.CreateFromTask(DictateMessage);
        }

        async Task SendMessage()
        {
            var handler = new OpenAIHttpClientHandler();
            OpenAIChatCompletionService chatCompletionService = new(OpenAIOption.Model, OpenAIOption.Key, httpClient: new HttpClient(handler));
            var msg=await chatCompletionService.GetChatMessageContentAsync(NewMessageContent);


            NewMessageContent = string.Empty;
        }

        async Task AttachImage()
        {

        }

        async Task DictateMessage()
        {
        }


        //Fields
        private ChatService chatService;
        private string newMessageContent;
        private WindowNotificationManager windowNotificationManager;
        private IObservable<bool> canSendMessage;
    }
}

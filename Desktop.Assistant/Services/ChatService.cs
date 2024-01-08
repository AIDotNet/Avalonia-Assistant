using Desktop.Assistant.Domain.Models;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Services
{
    public class ChatService : IChatService
    {
        private const string CHAT_HUB_NAME = "chat";

        public IObservable<HubConnectionState> ConnectionState { get; private set; }

        public IObservable<string> ParticipantLoggedIn { get; private set; }

        public IObservable<string> ParticipantLoggedOut { get; private set; }

        public IObservable<MessagePayload> MessageReceived { get; private set; }


        public ObservableCollection<MessagePayload> Messages { get; private set; } = new ObservableCollection<MessagePayload>();


        public ChatService(string serverUrl)
        {
            connection = new HubConnectionBuilder().WithUrl($"{serverUrl}{CHAT_HUB_NAME}").Build();
            connection.On<string>("UserLoggedIn", (userName) => participantLoggedInSubject.OnNext(userName));
            connection.On<string>("UserLoggedOut", (userName) => participantLoggedOutSubject.OnNext(userName));

            connection.On<MessagePayload>("NewMessage", (message) => ProcessNewMessage(message));


            ParticipantLoggedIn = participantLoggedInSubject.AsObservable();
            ParticipantLoggedOut = participantLoggedOutSubject.AsObservable();
            MessageReceived = newMessageReceivedSubject.AsObservable();
            ConnectionState = Observable.Interval(TimeSpan.FromMilliseconds(500))
                                        .Select(x => connection.State)
                                        .DistinctUntilChanged();
        }

        private void ProcessNewMessage(MessagePayload message)
        {
            Console.WriteLine($"{message.Type} Message Received");
            Messages.Add(message);
            newMessageReceivedSubject.OnNext(message);
        }

        public async Task ConnectAsync()
        {
            await connection.StartAsync();
        }

        public async Task<SuccessfulLoginResponse> LoginAsync(string username, string passcode)
        {
            var result = await connection.InvokeAsync<SuccessfulLoginResponse>("Login", username, passcode);
            ProcessLogInResponse(result);
            return result;
        }

        public async Task<SuccessfulLoginResponse> RegisterAndLogIn(string username, string passcode)
        {
            var result = await connection.InvokeAsync<SuccessfulLoginResponse>("RegisterAndLogIn", username, passcode);
            ProcessLogInResponse(result);
            return result;
        }

        public async Task SendMessageAsync(MessagePayload message)
        {
            if (string.IsNullOrEmpty(message.AuthorUsername))
                message.AuthorUsername = CurrentUser.UserName;

            await connection.InvokeAsync("SendMessage", message);
        }

        public async Task LogoutAsync()
        {
            await connection.InvokeAsync("Logout");
            Messages.Clear();
        }

        private void ProcessLogInResponse(SuccessfulLoginResponse slr)
        {
            currentUser = slr.User;
            if (slr.PreviousMessages != null)
            {
                foreach (var chat in slr.PreviousMessages) Messages.Add(chat);
            }
        }

        internal User CurrentUser => currentUser;
        
        //Fields 
        private User currentUser;
        private HubConnection connection;
        private Subject<MessagePayload> newMessageReceivedSubject = new Subject<MessagePayload>();
        private Subject<string> participantLoggedOutSubject = new Subject<string>();
        private Subject<string> participantLoggedInSubject = new Subject<string>();
    }
}

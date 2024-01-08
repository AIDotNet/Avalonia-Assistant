using Desktop.Assistant.Domain.Models;
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

        public IObservable<string> ParticipantLoggedIn { get; private set; }

        public IObservable<string> ParticipantLoggedOut { get; private set; }

        public IObservable<MessagePayload> MessageReceived { get; private set; }


        public ObservableCollection<MessagePayload> Messages { get; private set; } = new ObservableCollection<MessagePayload>();


        public ChatService(string serverUrl)
        {
          

            ParticipantLoggedIn = participantLoggedInSubject.AsObservable();
            ParticipantLoggedOut = participantLoggedOutSubject.AsObservable();
            MessageReceived = newMessageReceivedSubject.AsObservable();
       
        }

        private void ProcessNewMessage(MessagePayload message)
        {
            Console.WriteLine($"{message.Type} Message Received");
            Messages.Add(message);
            newMessageReceivedSubject.OnNext(message);
        }


        public async Task<SuccessfulLoginResponse> RegisterAndLogIn(string username, string passcode)
        {
            var result = new SuccessfulLoginResponse();
            ProcessLogInResponse(result);
            return result;
        }

        public async Task SendMessageAsync(MessagePayload message)
        {
            if (string.IsNullOrEmpty(message.AuthorUsername))
                message.AuthorUsername = CurrentUser.UserName;

        
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
        private Subject<MessagePayload> newMessageReceivedSubject = new Subject<MessagePayload>();
        private Subject<string> participantLoggedOutSubject = new Subject<string>();
        private Subject<string> participantLoggedInSubject = new Subject<string>();
    }
}

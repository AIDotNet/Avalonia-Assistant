using Desktop.Assistant.Domain.Models;
using System;
using System.Threading.Tasks;

namespace Desktop.Assistant.Services
{
    public interface IChatService
    {

        IObservable<string> ParticipantLoggedIn { get; }

        IObservable<string> ParticipantLoggedOut { get; }

        Task<SuccessfulLoginResponse> LoginAsync(string username, string passcode);

        Task<SuccessfulLoginResponse> RegisterAndLogIn(string username, string passcode);

        Task LogoutAsync();

        Task ConnectAsync();

        IObservable<MessagePayload> MessageReceived { get; }

        Task SendMessageAsync(MessagePayload message);
    }
}

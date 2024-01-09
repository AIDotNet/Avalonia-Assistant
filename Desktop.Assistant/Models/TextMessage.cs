using Desktop.Assistant.Domain.MessageTypes;
using Desktop.Assistant.Domain.Models;

namespace Desktop.Assistant.Models
{
    public class TextMessage : MessageBase
    {
        public string Content { get; init; }

        public TextMessage(string content)
        {
            Content = content;
        }



        internal override MessagePayload ToMessagePayload()
        {
            return new MessagePayload(ObjectToString(Content), MessageType.Text) { AuthorUsername = "你" };
        }

        internal TextMessage(MessagePayload payload)
        {
              Content = StringToObject<string>(payload.Base64Payload);
        }
             
    }
}

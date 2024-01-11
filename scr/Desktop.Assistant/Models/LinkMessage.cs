using System;
using Desktop.Assistant.Domain.MessageTypes;
using Desktop.Assistant.Domain.Models;

namespace Desktop.Assistant.Models
{
    public class LinkMessage : MessageBase
    {
        public string Url { get; init; }
        

        public LinkMessage(string url, string authorUsername)
        {
            Url = url;
            AuthorUsername = authorUsername;
        }

        internal LinkMessage(MessagePayload payload)
        {
            AuthorUsername = payload.AuthorUsername;
            Url = StringToObject<string>(payload.Base64Payload);
        }

        internal override MessagePayload ToMessagePayload()
        {
            return new MessagePayload(ObjectToString(Url), MessageType.Link);
        }
    }
}

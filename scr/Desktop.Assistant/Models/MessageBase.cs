using Desktop.Assistant.Domain.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace Desktop.Assistant.Models
{
    public abstract class MessageBase
    {
        public string AuthorUsername { get; init; }

        protected string ObjectToString(object obj)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                var jsonString = JsonConvert.SerializeObject(obj);
                return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jsonString));
            }
        }

        internal T StringToObject<T>(string base64String)
        {
            var jsonBytes = Convert.FromBase64String(base64String);
            var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes);
            return JsonConvert.DeserializeObject<T>(jsonString);
        }

        public ChatRoleType Role { get; init; }

        public bool IsRead { get; set; }

        internal abstract MessagePayload ToMessagePayload();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Models
{
    public class SendMessageInput
    {
        public string Text { get; set; } = "";
    }

    public class HistoryInput
    {
        public List<HistoryItem> Messages { get; set; } = [];
        public class HistoryItem
        {
            public string Role { get; set; } = "User";
            public string Content { get; set; } = "";
        }
    }
}

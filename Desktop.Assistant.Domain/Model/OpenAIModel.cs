using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.Model
{
    public class OpenAIModel
    {
        public string EndPoint{ get; set; }
        public string Key { get; set; }
        public string Model { get; set; }
    }
}

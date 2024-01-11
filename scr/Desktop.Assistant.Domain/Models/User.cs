using System;
using System.Collections.Generic;

namespace Desktop.Assistant.Domain.Models
{
    public class User
    {
        public string Id { get; init; }

        public string UserName { get; set; }

        public ImageUrls Avatar { get; set; }

        public List<Connection> ActiveConnections { get; set; }

        public PassCode PassCode { get; init; }

        public User()
        {
            Id = Guid.NewGuid().ToString();
            ActiveConnections = new List<Connection>();
        }

        public User(string id, string username, PassCode passCode) : this()
        {
            Id = id;
            UserName = username;
            PassCode = passCode;
        }
    }

    public class Connection
    {
        public string Id { get; set; }

        public bool Active { get; set; }
    }
}

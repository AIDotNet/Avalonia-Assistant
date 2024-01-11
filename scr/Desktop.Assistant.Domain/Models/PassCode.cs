using System;

namespace Desktop.Assistant.Domain.Models
{
    public class PassCode
    {
        public string Id { get; init; }

        public string OwnerId { get; init; }

        public byte[] SaltedPassCode { get; init; }

        public byte[] Salt { get; init; }


        public PassCode()
        {
            Id = Guid.NewGuid().ToString();
        }

        public PassCode(string ownerId, byte[] saltedPassCode, byte[] salt)
        {
            Id = Guid.NewGuid().ToString();
            OwnerId = ownerId;
            SaltedPassCode = saltedPassCode;
            Salt = salt;
        }
    }

    public class AccessToken
    {
        public string Id { get; init; }

        public string OwnerId { get; init; }

        public string Token { get; init; }

        public AccessToken(string ownerId, string token) : this()
        {
            OwnerId = ownerId;
            Token = token;
        }

        public AccessToken()
        {
            Id = Guid.NewGuid().ToString();
        }
    }
}

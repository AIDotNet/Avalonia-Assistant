using System;

namespace Desktop.Assistant.Domain.Models
{
    public class ImageUrls
    {
        public string Id { get; init; }

        public Uri Original { get; set; }

        public Uri Medium { get; set; }

        public Uri Small { get; set; }

        public Uri Icon { get; set; }

        public string Description { get; set; }

        public ImageUrls()
        {
            Id = Guid.NewGuid().ToString();
        }
    }
}

using System.Text;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>读 pptx：按页给文字，并把演讲者备注带上——备注常常是整页里信息量最大的部分。</summary>
internal static class PowerPointRead
{
    public static string Read(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentationPart = document.PresentationPart;
        if (presentationPart?.Presentation?.SlideIdList is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var index = 1;

        foreach (var slideId in presentationPart.Presentation.SlideIdList!.Elements<P.SlideId>())
        {
            if (slideId.RelationshipId?.Value is not { } relationshipId)
            {
                continue;
            }

            if (presentationPart.GetPartById(relationshipId) is not SlidePart slidePart)
            {
                continue;
            }

            builder.Append("## 第 ").Append(index).AppendLine(" 页");

            foreach (var paragraph in slidePart.Slide?.Descendants<A.Paragraph>() ?? [])
            {
                var text = string.Concat(paragraph.Descendants<A.Text>().Select(item => item.Text));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text.Trim());
                }
            }

            var notes = slidePart.NotesSlidePart?.NotesSlide?
                .Descendants<A.Text>()
                .Select(item => item.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            if (notes is { Count: > 0 })
            {
                builder.AppendLine("备注：" + string.Join(" ", notes).Trim());
            }

            builder.AppendLine();
            index++;
        }

        return builder.ToString().TrimEnd();
    }
}

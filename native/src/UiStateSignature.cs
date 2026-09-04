using System.Security.Cryptography;
using System.Text;

namespace ComputerUse.Native;

internal static class UiStateSignature
{
    public static string Compute(ObserveResult observation)
    {
        var builder = new StringBuilder();
        builder.Append(observation.Window.Id)
            .Append('\u001f')
            .Append(observation.Window.Title)
            .Append('\u001f')
            .Append(observation.Window.X)
            .Append(',')
            .Append(observation.Window.Y)
            .Append(',')
            .Append(observation.Window.Width)
            .Append(',')
            .Append(observation.Window.Height)
            .Append('\u001f')
            .Append(observation.ScreenshotHash ?? string.Empty)
            .Append('\u001e');

        foreach (var element in observation.Elements)
        {
            builder.Append(element.Role)
                .Append('\u001f')
                .Append(element.Name)
                .Append('\u001f')
                .Append(element.AutomationId)
                .Append('\u001f')
                .Append(element.Value ?? string.Empty)
                .Append('\u001f')
                .Append(element.IsEnabled)
                .Append('\u001f')
                .Append(string.Join(',', element.Bounds))
                .Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

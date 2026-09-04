using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputerUse.Native.Rpc;

namespace ComputerUse.Native;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ComputerUse.Native is Windows-only.");
            return 2;
        }

        DpiAwareness.EnablePerMonitorV2();

        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        using var runtime = new NativeRuntime();

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RpcResponse response;
            JsonNode? requestId = null;

            try
            {
                var request = RpcRequest.Parse(line);
                requestId = request.Id;
                response = runtime.Handle(request);
            }
            catch (ComputerUseException exception)
            {
                response = RpcResponse.Failure(requestId, exception.Code, exception.Message, exception.Details);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Computer Use for Antigravity: unexpected error: {exception}");
                response = RpcResponse.Failure(requestId, "INTERNAL_ERROR", exception.Message);
            }

            Console.WriteLine(JsonSerializer.Serialize(response, serializerOptions));
            Console.Out.Flush();
        }

        return 0;
    }
}

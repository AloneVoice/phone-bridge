using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using PhoneBridge.Core;

namespace PhoneBridge.App;

internal sealed record IpcMessage(string Command, string[] Paths)
{
    public const string Send = "send";
    public const string Show = "show";
}

/// <summary>Lokalny kanał menu kontekstowe → agent. Potok dostępny tylko dla bieżącego użytkownika.</summary>
internal static class Ipc
{
    private static readonly string PipeName = $"PhoneBridge.{WindowsIdentity.GetCurrent().User!.Value}";

    public static bool TrySend(IpcMessage message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            JsonSerializer.Serialize(pipe, message);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task ServeAsync(Action<IpcMessage> onMessage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = HandleAsync(pipe, onMessage);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, Action<IpcMessage> onMessage)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var message = await JsonSerializer.DeserializeAsync<IpcMessage>(pipe, cancellationToken: timeout.Token);
                if (message is not null)
                    onMessage(message);
            }
            catch (Exception e) when (e is JsonException or IOException or OperationCanceledException)
            {
                Log.Error("Nieprawidłowa wiadomość IPC", e);
            }
        }
    }
}

namespace Packo.Services.Interfaces
{
    public interface IAIService
    {
        Task<string> GetCompletionAsync(string prompt);
        Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken) =>
            GetCompletionAsync(prompt).WaitAsync(cancellationToken);
    }
}

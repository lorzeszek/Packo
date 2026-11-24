namespace Packo.Services.Interfaces;

public interface IPlacesTransport
{
    Task<HttpResponseMessage> SendAsync(object body, CancellationToken token);
}

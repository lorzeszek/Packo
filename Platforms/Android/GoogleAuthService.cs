using Android.OS;
using Android.Runtime;
using AndroidX.Credentials;
using Java.Util.Concurrent;
using Packo.Interfaces;
using Xamarin.GoogleAndroid.Libraries.Identity.GoogleId;

namespace Packo.Platforms.Android
{
    internal class GoogleAuthService : IGoogleAuthService
    {
        public async Task<string?> SignInWithGoogleAsync()
        {
            var activity = Platform.CurrentActivity ?? throw new Exception("Brak aktywnej Activity");
            var context = activity;

            var credentialManager = CredentialManager.Create(context);

            //var googleIdOption = new GetGoogleIdOption.Builder()
            //    .SetFilterByAuthorizedAccounts(false)
            //    .SetServerClientId("249767082828-93lqcq1afkb7ms7s3gosusr8e2f4sjmn.apps.googleusercontent.com")
            //    .Build();

            var googleIdOption = new GetGoogleIdOption.Builder()
                .SetFilterByAuthorizedAccounts(false)
                .SetServerClientId("582002118390-sgjttehmfqkj3g6opif9fkfgmpmmi90d.apps.googleusercontent.com")
                .Build();

            var request = new GetCredentialRequest.Builder()
                .AddCredentialOption(googleIdOption)
                .Build();

            var tcs = new TaskCompletionSource<GetCredentialResponse>();

            var callback = new GoogleCredentialCallback(tcs);

            var cancellation = new CancellationSignal();

            // Executor — pojedynczy wątek UI

            var executor = new UiThreadExecutor();

            // Wywołanie zgodnie z twoją sygnaturą 5-argumentową:
            credentialManager.GetCredentialAsync(
                context,
                request,
                cancellation,
                executor,
                callback);

            // asynchroniczne oczekiwanie na wynik callbacku
            var response = await tcs.Task;

            if (response.Credential is CustomCredential cred &&
                cred.Type == GoogleIdTokenCredential.TypeGoogleIdTokenCredential)
            {
                var tokenResult = GoogleIdTokenCredential.CreateFrom(cred.Data);
                return tokenResult.IdToken;
            }

            return null;
        }
    }
}

public class UiThreadExecutor : Java.Lang.Object, IExecutor
{
    public void Execute(Java.Lang.IRunnable command)
    {
        Platform.CurrentActivity?.RunOnUiThread(command.Run);
    }
}

public class GoogleCredentialCallback : Java.Lang.Object, ICredentialManagerCallback
{
    private readonly TaskCompletionSource<GetCredentialResponse> _tcs;

    public GoogleCredentialCallback(TaskCompletionSource<GetCredentialResponse> tcs)
    {
        _tcs = tcs;
    }

    public void OnError(Java.Lang.Object error)
    {
        _tcs.TrySetException(new Exception(error?.ToString()));
    }

    public void OnResult(Java.Lang.Object result)
    {
        _tcs.TrySetResult(result.JavaCast<GetCredentialResponse>());
    }
}

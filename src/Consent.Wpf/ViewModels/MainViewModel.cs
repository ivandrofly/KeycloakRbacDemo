using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Consent.Wpf.Auth;

namespace Consent.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConsentAuthService _auth = new();
    private ConsentSession? _session;

    // --- what to ask for (editable while signed out) ----------------------------------------
    [ObservableProperty] public partial bool RequestAddress { get; set; } = true;
    [ObservableProperty] public partial bool RequestPhone { get; set; } = true;
    [ObservableProperty] public partial bool RequestOfflineAccess { get; set; }

    public string BaseScopesText { get; } = string.Join(' ', ConsentAuthService.BaseScopes);

    // --- identity -----------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedOut))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(LogoutCommand), nameof(RevokeConsentCommand))]
    public partial bool IsLoggedIn { get; private set; }

    public bool IsLoggedOut => !IsLoggedIn;

    [ObservableProperty] public partial string UserName { get; private set; } = string.Empty;
    [ObservableProperty] public partial string TokenExpires { get; private set; } = string.Empty;
    [ObservableProperty] public partial string RequestedScopesText { get; private set; } = string.Empty;

    public ObservableCollection<string> GrantedScopes { get; } = [];
    public ObservableCollection<ClaimRow> Claims { get; } = [];

    [ObservableProperty] public partial string StatusMessage { get; private set; } = "Not signed in. Pick the optional scopes, then sign in.";
    [ObservableProperty] public partial bool StatusIsError { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    // --- commands ---------------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(IsLoggedOut))]
    private async Task LoginAsync()
    {
        var optional = SelectedOptionalScopes().ToList();
        RequestedScopesText = string.Join(' ', ConsentAuthService.BaseScopes.Concat(optional));

        await RunAsync("Waiting for you to sign in (and consent) in the browser...", async () =>
        {
            ApplySession(await _auth.LoginAsync(optional));
            SetStatus($"Signed in as {UserName}. Keycloak granted {GrantedScopes.Count} scope(s).");
        });
    }

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private async Task LogoutAsync()
    {
        var session = _session;
        ApplySession(null);
        SetStatus("Signed out. Consent is still stored: the next sign-in will not ask again.");
        if (session is not null)
        {
            try
            {
                await _auth.LogoutAsync(session);
            }
            catch (Exception ex)
            {
                SetStatus($"Signed out locally; Keycloak logout failed: {ex.Message}", error: true);
            }
        }
    }

    /// <summary>
    /// Removes the stored consent in Keycloak, then signs out locally so the next login goes through the
    /// consent page again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private Task RevokeConsentAsync() => RunAsync("Revoking consent in Keycloak...", async () =>
    {
        await _auth.RevokeConsentAsync(_session!);
        var user = UserName;
        ApplySession(null);
        SetStatus($"Consent for {user} revoked. Sign in again and Keycloak will show the consent page.");
    });

    [RelayCommand]
    private void OpenAccountConsole() => ConsentAuthService.OpenAccountConsole();

    // --- helpers ----------------------------------------------------------------------------
    private IEnumerable<string> SelectedOptionalScopes()
    {
        if (RequestAddress)
        {
            yield return "address";
        }
        if (RequestPhone)
        {
            yield return "phone";
        }
        if (RequestOfflineAccess)
        {
            yield return "offline_access";
        }
    }

    private void ApplySession(ConsentSession? session)
    {
        _session = session;
        IsLoggedIn = session is not null;
        UserName = session?.UserName ?? string.Empty;
        TokenExpires = session is null ? string.Empty : $"token expires {session.ExpiresAt.ToLocalTime():HH:mm:ss}";

        GrantedScopes.Clear();
        Claims.Clear();
        if (session is null)
        {
            return;
        }

        foreach (var scope in session.GrantedScopes)
        {
            GrantedScopes.Add(scope);
        }
        foreach (var claim in session.IdTokenClaims)
        {
            Claims.Add(claim);
        }
    }

    private async Task RunAsync(string busyMessage, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        SetStatus(busyMessage);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusMessage = message;
        StatusIsError = error;
    }
}

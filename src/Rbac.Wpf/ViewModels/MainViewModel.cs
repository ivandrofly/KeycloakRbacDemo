using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rbac.Wpf.Api;
using Rbac.Wpf.Auth;

namespace Rbac.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AuthService _auth = new();
    private readonly ApiClient _api = new();
    private UserSession? _session;

    // --- identity & capabilities: everything the XAML binds Visibility to -------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedOut))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(LogoutCommand))]
    public partial bool IsLoggedIn { get; private set; }

    public bool IsLoggedOut => !IsLoggedIn;

    [ObservableProperty] public partial string UserName { get; private set; } = string.Empty;
    [ObservableProperty] public partial string TokenExpires { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsAdmin { get; private set; }
    [ObservableProperty] public partial bool CanReadOrders { get; private set; }
    [ObservableProperty] public partial bool CanWriteOrders { get; private set; }
    [ObservableProperty] public partial bool CanDeleteOrders { get; private set; }

    public ObservableCollection<string> Roles { get; } = [];
    public ObservableCollection<string> Permissions { get; } = [];

    // --- data ------------------------------------------------------------------------------
    public ObservableCollection<Order> Orders { get; } = [];
    public ObservableCollection<AuditEntry> AuditEntries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateOrderCommand))]
    public partial string NewOrderCustomer { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateOrderCommand))]
    public partial string NewOrderAmount { get; set; } = string.Empty;

    [ObservableProperty] public partial string StatusMessage { get; private set; } = "Not signed in.";
    [ObservableProperty] public partial bool StatusIsError { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    // --- commands --------------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(IsLoggedOut))]
    private async Task LoginAsync()
    {
        await RunAsync("Waiting for you to sign in in the browser...", async () =>
        {
            ApplySession(await _auth.LoginAsync());
            SetStatus($"Signed in as {UserName}.");
            if (CanReadOrders)
            {
                await LoadOrdersCoreAsync();
            }
            if (IsAdmin)
            {
                await LoadAuditCoreAsync();
            }
        });
    }

    [RelayCommand(CanExecute = nameof(IsLoggedIn))]
    private async Task LogoutAsync()
    {
        var session = _session;
        ApplySession(null);
        SetStatus("Signed out.");
        if (session is not null)
        {
            try { await _auth.LogoutAsync(session); }
            catch (Exception ex) { SetStatus($"Signed out locally; Keycloak logout failed: {ex.Message}", error: true); }
        }
    }

    [RelayCommand]
    private Task LoadOrdersAsync() => RunAsync("Loading orders...", LoadOrdersCoreAsync);

    [RelayCommand(CanExecute = nameof(CanCreateOrder))]
    private Task CreateOrderAsync() => RunAsync("Creating order...", async () =>
    {
        var result = await _api.CreateOrderAsync(NewOrderCustomer.Trim(), decimal.Parse(NewOrderAmount));
        if (!result.IsSuccess) { SetStatus($"Create order -> {result.StatusText}", error: true); return; }

        Orders.Add(result.Value!);
        NewOrderCustomer = string.Empty;
        NewOrderAmount = string.Empty;
        SetStatus($"Order #{result.Value!.Id} created ({result.StatusText}).");
        if (IsAdmin)
        {
            await LoadAuditCoreAsync();
        }
    });

    private bool CanCreateOrder() =>
        !string.IsNullOrWhiteSpace(NewOrderCustomer) && decimal.TryParse(NewOrderAmount, out var a) && a > 0;

    [RelayCommand]
    private Task DeleteOrderAsync(Order order) => RunAsync($"Deleting order #{order.Id}...", async () =>
    {
        var result = await _api.DeleteOrderAsync(order.Id);
        if (!result.IsSuccess) { SetStatus($"Delete order -> {result.StatusText}", error: true); return; }

        Orders.Remove(order);
        SetStatus($"Order #{order.Id} deleted ({result.StatusText}).");
        if (IsAdmin)
        {
            await LoadAuditCoreAsync();
        }
    });

    [RelayCommand]
    private Task LoadAuditAsync() => RunAsync("Loading audit log...", LoadAuditCoreAsync);

    /// <summary>
    /// Always visible on purpose: proves the server enforces the rules even if a client
    /// ignores the UI gating. Non-admins get 403 Forbidden.
    /// </summary>
    [RelayCommand]
    private Task TryAdminCallAsync() => RunAsync("Calling admin endpoint...", async () =>
    {
        var result = await _api.GetAuditAsync();
        SetStatus(result.IsSuccess
                ? $"GET /api/admin/audit -> {result.StatusText}: server allowed it, you are an admin."
                : $"GET /api/admin/audit -> {result.StatusText}: server refused it (as expected for non-admins).",
            error: !result.IsSuccess);
    });

    // --- helpers ---------------------------------------------------------------------------
    private async Task LoadOrdersCoreAsync()
    {
        var result = await _api.GetOrdersAsync();
        if (!result.IsSuccess) { SetStatus($"Load orders -> {result.StatusText}", error: true); return; }
        Orders.Clear();
        foreach (var o in result.Value!)
        {
            Orders.Add(o);
        }
        SetStatus($"Loaded {Orders.Count} order(s).");
    }

    private async Task LoadAuditCoreAsync()
    {
        var result = await _api.GetAuditAsync();
        if (!result.IsSuccess) { SetStatus($"Load audit -> {result.StatusText}", error: true); return; }
        AuditEntries.Clear();
        foreach (var e in result.Value!)
        {
            AuditEntries.Add(e);
        }
    }

    /// <param name="keepData">On a token refresh the loaded orders/audit entries stay; only identity and flags are recomputed.</param>
    private void ApplySession(UserSession? session, bool keepData = false)
    {
        _session = session;
        _api.SetAccessToken(session?.AccessToken);

        IsLoggedIn = session is not null;
        UserName = session?.UserName ?? string.Empty;
        TokenExpires = session is null ? string.Empty : $"token expires {session.ExpiresAt.ToLocalTime():HH:mm:ss}";

        // All four flags are Keycloak Authorization Services decisions (resource#scope granted on rbac-wpf).
        // The admin card follows "Audit log#audit:read", which the 'Is admin' policy grants.
        IsAdmin = session?.HasScope("audit:read") ?? false;
        CanReadOrders = session?.HasScope("order:read") ?? false;
        CanWriteOrders = session?.HasScope("order:write") ?? false;
        CanDeleteOrders = session?.HasScope("order:delete") ?? false;

        Roles.Clear();
        Permissions.Clear();
        if (!keepData)
        {
            Orders.Clear();
            AuditEntries.Clear();
        }
        if (session is null)
        {
            return;
        }

        foreach (var r in session.Roles)
        {
            Roles.Add(r);
        }
        foreach (var p in session.Permissions)
        {
            Permissions.Add(p);
        }
    }

    private async Task EnsureFreshTokenAsync()
    {
        if (_session is null || _session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return;
        }
        // Re-asks Keycloak for permissions too, so a role change made in the admin console shows up after refresh.
        ApplySession(await _auth.RefreshAsync(_session), keepData: true);
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
            if (IsLoggedIn)
            {
                await EnsureFreshTokenAsync();
            }
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

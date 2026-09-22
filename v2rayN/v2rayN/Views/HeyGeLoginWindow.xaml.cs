using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Views;

/// <summary>
/// HeyGe-only bootstrap for a single managed subscription.  It exchanges the
/// password over HTTPS for a narrowly scoped bearer URL and never persists the
/// password itself.
/// </summary>
public partial class HeyGeLoginWindow : Window
{
    private const string ManagedSubscriptionMemo = "HEYGE_V2RAYN_MANAGED";

    public HeyGeLoginWindow()
    {
        InitializeComponent();
        btnLogin.Click += BtnLogin_Click;
        btnCancel.Click += (_, _) => DialogResult = false;
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        txtStatus.Text = string.Empty;
        btnLogin.IsEnabled = false;
        try
        {
            var origin = ParseHttpsOrigin(txtSite.Text);
            var email = txtEmail.Text.Trim();
            var password = txtPassword.Password;
            if (email.IsNullOrEmpty() || password.IsNullOrEmpty())
                throw new InvalidOperationException("请填写网站账号和密码");

            using var client = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.PostAsJsonAsync(
                "/api/customer/auth/v2rayn/login",
                new HeyGeLoginRequest(email, password));
            var payload = await response.Content.ReadFromJsonAsync<HeyGeLoginResponse>();
            if (!response.IsSuccessStatusCode || payload is null || string.IsNullOrEmpty(payload.SubscriptionPath))
            {
                var message = payload?.Message;
                throw new InvalidOperationException(message.IsNullOrEmpty() ? "登录失败，请检查账号、密码和网站地址" : message);
            }

            if (!payload.SubscriptionPath.StartsWith("/api/customer/v2rayn/subscription/", StringComparison.Ordinal))
                throw new InvalidOperationException("服务器返回了无效的订阅地址");
            var subscriptionUrl = new System.Uri(
                origin.AbsoluteUri.TrimEnd('/') + payload.SubscriptionPath,
                UriKind.Absolute).AbsoluteUri;
            var existing = (await AppManager.Instance.SubItems())
                .FirstOrDefault(item => item.Memo == ManagedSubscriptionMemo);
            var item = existing ?? new SubItem();
            item.Remarks = payload.GroupName.IsNullOrEmpty() ? "HeyGe · 我的有效节点" : payload.GroupName;
            item.Url = subscriptionUrl;
            item.Enabled = true;
            item.Memo = ManagedSubscriptionMemo;
            item.UserAgent = "HeyGe-v2rayN";

            if (await ConfigHandler.AddSubItem(AppManager.Instance.Config, item) != 0)
                throw new InvalidOperationException("无法保存订阅，请检查客户端本地存储权限");

            // Sync now so the user sees one up-to-date HeyGe group immediately.
            await SubscriptionHandler.UpdateProcess(
                AppManager.Instance.Config,
                item.Id,
                false,
                (_, message) =>
                {
                    Dispatcher.Invoke(() => txtStatus.Text = message);
                    return Task.CompletedTask;
                });
            txtPassword.Clear();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            txtPassword.Clear();
            txtStatus.Text = ex.Message;
            btnLogin.IsEnabled = true;
        }
    }

    private static Uri ParseHttpsOrigin(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
            throw new InvalidOperationException("网站地址必须是 HTTPS 根地址，例如 https://ananx.cc");
        return uri;
    }

    private sealed record HeyGeLoginRequest(string Email, string Password);

    private sealed record HeyGeLoginResponse(
        [property: JsonPropertyName("groupName")] string? GroupName,
        [property: JsonPropertyName("subscriptionPath")] string? SubscriptionPath,
        [property: JsonPropertyName("message")] string? Message);
}

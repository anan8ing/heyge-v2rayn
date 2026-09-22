using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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

    public bool SuppressStartupPrompt => chkDontPromptAgain.IsChecked == true;

    public static async Task<bool> HasManagedSubscriptionAsync()
    {
        return (await AppManager.Instance.SubItems())
            .Any(item => item.Memo == ManagedSubscriptionMemo && item.Enabled != false);
    }

    public HeyGeLoginWindow()
    {
        InitializeComponent();
        btnLogin.Click += BtnLogin_Click;
        btnCancel.Click += (_, _) => DialogResult = false;
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("正在验证账号…", "#1565C0");
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
            var responseBody = await response.Content.ReadAsStringAsync();
            HeyGeLoginResponse? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<HeyGeLoginResponse>(responseBody);
            }
            catch (JsonException)
            {
                // Validation responses may contain an array of messages rather
                // than the normal string message. It is handled below.
            }
            if (!response.IsSuccessStatusCode || payload is null || string.IsNullOrEmpty(payload.SubscriptionPath))
            {
                var message = payload?.Message ?? ReadResponseMessage(responseBody);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    message = "账号或密码错误，请确认后重试";
                else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    message ??= "账号或密码格式不正确，请检查后重试";
                throw new InvalidOperationException(message.IsNullOrEmpty() ? $"登录失败（HTTP {(int)response.StatusCode}），请检查网站地址或稍后重试" : message);
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
            var syncSucceeded = false;
            await SubscriptionHandler.UpdateProcess(
                AppManager.Instance.Config,
                item.Id,
                false,
                (success, message) =>
                {
                    Dispatcher.Invoke(() => SetStatus(message, success ? "#188038" : "#1565C0"));
                    syncSucceeded |= success;
                    return Task.CompletedTask;
                });
            if (!syncSucceeded)
                throw new InvalidOperationException("已验证账号，但同步有效节点失败。请检查网络后重试，或联系客户服务。");

            SetStatus("登录成功，已同步有效节点。", "#188038");
            txtPassword.Clear();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // Keep the password in the field so a transient network failure
            // can be retried without re-entering it; it is never saved.
            SetStatus(ex.Message, "#B42318");
        }
        finally
        {
            btnLogin.IsEnabled = true;
        }
    }

    private void SetStatus(string message, string color)
    {
        txtStatus.Text = message;
        txtStatus.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    }

    private static string? ReadResponseMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("message", out var message))
                return null;
            return message.ValueKind switch
            {
                JsonValueKind.String => message.GetString(),
                JsonValueKind.Array => string.Join("；", message.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
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

using System.Diagnostics;

namespace VrSessionMonitor.Tray;

#if INCLUDE_HOME_ASSISTANT
/// <summary>Minimal Base URL + Access Token entry so setting up the Home Assistant connection
/// doesn't require hand-editing appsettings.json. Access Token is masked (UseSystemPasswordChar)
/// since it's a long-lived credential worth not leaving visible on screen, not because this app
/// otherwise treats it as sensitive (it's saved to appsettings.json in plain text either way, same
/// as every other setting).
///
/// The "Create token" button lives inside this dialog (not as a separate tray menu item pointing at
/// already-saved config) specifically to avoid a chicken-and-egg ordering problem confirmed live
/// 2026-07-29: a standalone "Get access token" item needs BaseUrl already saved to build the URL,
/// but the whole point of getting a token first is that nothing's saved yet. This button uses
/// whatever's currently typed in the Base URL box, saved or not.</summary>
public sealed class HomeAssistantSetupDialog : Form
{
    private const string BaseUrlExample = "http://homeassistant.local:8123";

    public string BaseUrl { get; private set; } = "";
    public string AccessToken { get; private set; } = "";

    private readonly TextBox _baseUrlBox;
    private readonly TextBox _tokenBox;

    public HomeAssistantSetupDialog(string currentBaseUrl, string currentToken)
    {
        Text = "Home Assistant connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new System.Drawing.Size(460, 172);

        var baseUrlLabel = new Label { Text = "Base URL:", Left = 12, Top = 15, Width = 90 };
        _baseUrlBox = new TextBox
        {
            Left = 110, Top = 12, Width = 330,
            Text = string.IsNullOrWhiteSpace(currentBaseUrl) ? BaseUrlExample : currentBaseUrl,
        };

        var tokenLabel = new Label { Text = "Access Token:", Left = 12, Top = 50, Width = 90 };
        _tokenBox = new TextBox { Left = 110, Top = 47, Width = 225, Text = currentToken, UseSystemPasswordChar = true };
        var createTokenButton = new Button { Text = "Create token", Left = 342, Top = 46, Width = 98, Height = 23 };
        createTokenButton.Click += (_, _) => OpenTokenPage();

        var tokenHintLabel = new Label
        {
            Text = "On that page, scroll down to \"Long-lived access tokens\" to create one.",
            Left = 110, Top = 72, Width = 330, Height = 16,
            ForeColor = System.Drawing.SystemColors.GrayText,
            Font = new System.Drawing.Font(Font.FontFamily, 7.5f),
        };

        var okButton = new Button { Text = "Connect", Left = 260, Top = 117, Width = 85, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Left = 355, Top = 117, Width = 85, DialogResult = DialogResult.Cancel };

        okButton.Click += (_, _) =>
        {
            BaseUrl = _baseUrlBox.Text.Trim();
            AccessToken = _tokenBox.Text.Trim();
        };

        Controls.Add(baseUrlLabel);
        Controls.Add(_baseUrlBox);
        Controls.Add(tokenLabel);
        Controls.Add(_tokenBox);
        Controls.Add(createTokenButton);
        Controls.Add(tokenHintLabel);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void OpenTokenPage()
    {
        var baseUrl = _baseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            MessageBox.Show(this, "Type your Home Assistant Base URL above first.", "Home Assistant",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo($"{baseUrl.TrimEnd('/')}/profile/security") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the token page: {ex.Message}", "Home Assistant",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
#endif

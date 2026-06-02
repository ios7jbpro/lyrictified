#if DEBUG
using System.Diagnostics;

namespace Lyrictified.Services;

internal static class DebugBuildHelper
{
    public static string? ShowDialog()
    {
        using var form = new System.Windows.Forms.Form
        {
            Text = "Lyrictified - Debug Build",
            ClientSize = new System.Drawing.Size(540, 260),
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = System.Drawing.Color.FromArgb(22, 27, 34),
        };

        var label = new System.Windows.Forms.Label
        {
            Text = "You are running a debug build. We suggest that if you are editing or developing this app further, you run it alongside Lyrictified-Server, so that you can troubleshoot the whole ecosystem.\n\nIf you already are, pick it from below. If you are not, pick the other option that will use the public URL instead.",
            ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
            BackColor = form.BackColor,
            AutoSize = false,
            Size = new System.Drawing.Size(500, 130),
            Location = new System.Drawing.Point(20, 15),
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
        };
        form.Controls.Add(label);

        string? result = null;

        var btnLocal = new System.Windows.Forms.Button
        {
            Text = "Use local URL",
            Size = new System.Drawing.Size(120, 32),
            Location = new System.Drawing.Point(20, 170),
            ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
            BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnLocal.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
        btnLocal.Click += (_, _) =>
        {
            var auto = LocalServerDetector.TryAutoDetect();
            if (auto is not null)
            {
                result = auto;
                App.IgnoreLocalCache = ShowCacheDialog(form);
                form.DialogResult = System.Windows.Forms.DialogResult.OK;
                form.Close();
                return;
            }

            using var manualForm = new System.Windows.Forms.Form
            {
                Text = "Local Server Not Found",
                ClientSize = new System.Drawing.Size(380, 140),
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = form.BackColor,
            };

            var manualLabel = new System.Windows.Forms.Label
            {
                Text = "Could not auto-detect Lyrictified-Server.\nPlease enter the local IP:port (e.g. 127.0.0.1:32145):",
                ForeColor = label.ForeColor,
                BackColor = manualForm.BackColor,
                AutoSize = false,
                Size = new System.Drawing.Size(350, 40),
                Location = new System.Drawing.Point(15, 12),
                Font = label.Font,
            };
            manualForm.Controls.Add(manualLabel);

            var textBox = new System.Windows.Forms.TextBox
            {
                Text = "127.0.0.1:32145",
                Location = new System.Drawing.Point(15, 56),
                Size = new System.Drawing.Size(350, 23),
                ForeColor = System.Drawing.Color.Black,
                BackColor = System.Drawing.Color.White,
            };
            manualForm.Controls.Add(textBox);

            var okBtn = new System.Windows.Forms.Button
            {
                Text = "OK",
                DialogResult = System.Windows.Forms.DialogResult.OK,
                Size = new System.Drawing.Size(80, 28),
                Location = new System.Drawing.Point(180, 95),
                ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
                BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            };
            okBtn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
            manualForm.Controls.Add(okBtn);
            manualForm.AcceptButton = okBtn;

            var cancelBtn = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                DialogResult = System.Windows.Forms.DialogResult.Cancel,
                Size = new System.Drawing.Size(80, 28),
                Location = new System.Drawing.Point(275, 95),
                ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
                BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            };
            cancelBtn.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
            manualForm.Controls.Add(cancelBtn);
            manualForm.CancelButton = cancelBtn;

            var dr = manualForm.ShowDialog(form);
            if (dr == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                var input = textBox.Text.Trim();
                if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    input = "http://" + input;
                }
                if (!input.EndsWith('/'))
                {
                    input += "/";
                }
                result = input;
                App.IgnoreLocalCache = ShowCacheDialog(form);
                form.DialogResult = System.Windows.Forms.DialogResult.OK;
                form.Close();
            }
        };
        form.Controls.Add(btnLocal);

        var btnPublic = new System.Windows.Forms.Button
        {
            Text = "Use public URL",
            Size = new System.Drawing.Size(120, 32),
            Location = new System.Drawing.Point(150, 170),
            ForeColor = btnLocal.ForeColor,
            BackColor = btnLocal.BackColor,
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnPublic.FlatAppearance.BorderColor = btnLocal.FlatAppearance.BorderColor;
        btnPublic.Click += (_, _) =>
        {
            result = "https://lyrictifiedserve.ios7.xyz/";
            form.DialogResult = System.Windows.Forms.DialogResult.OK;
            form.Close();
        };
        form.Controls.Add(btnPublic);

        var btnGet = new System.Windows.Forms.Button
        {
            Text = "Get Lyrictified-Server",
            Size = new System.Drawing.Size(150, 32),
            Location = new System.Drawing.Point(280, 170),
            ForeColor = btnLocal.ForeColor,
            BackColor = btnLocal.BackColor,
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnGet.FlatAppearance.BorderColor = btnLocal.FlatAppearance.BorderColor;
        btnGet.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo("https://github.com/ios7jbpro/Lyrictified-Server")
            {
                UseShellExecute = true
            });
        };
        form.Controls.Add(btnGet);

        var btnExit = new System.Windows.Forms.Button
        {
            Text = "Exit",
            Size = new System.Drawing.Size(80, 32),
            Location = new System.Drawing.Point(440, 170),
            ForeColor = btnLocal.ForeColor,
            BackColor = btnLocal.BackColor,
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnExit.FlatAppearance.BorderColor = btnLocal.FlatAppearance.BorderColor;
        btnExit.Click += (_, _) =>
        {
            result = null;
            form.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            form.Close();
        };
        form.Controls.Add(btnExit);

        form.ShowDialog();
        return result;
    }

    private static bool ShowCacheDialog(System.Windows.Forms.Form parent)
    {
        using var cacheForm = new System.Windows.Forms.Form
        {
            Text = "Cache Settings",
            ClientSize = new System.Drawing.Size(460, 160),
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = System.Drawing.Color.FromArgb(22, 27, 34),
        };

        var cacheLabel = new System.Windows.Forms.Label
        {
            Text = "Do you want to ignore/disable in-app local cache as well? If you do not, the app will ignore the server for already-cached lyrics.",
            ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
            BackColor = cacheForm.BackColor,
            AutoSize = false,
            Size = new System.Drawing.Size(420, 60),
            Location = new System.Drawing.Point(20, 15),
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
        };
        cacheForm.Controls.Add(cacheLabel);

        bool ignoreCache = false;

        var btnYes = new System.Windows.Forms.Button
        {
            Text = "Yes",
            Size = new System.Drawing.Size(80, 32),
            Location = new System.Drawing.Point(130, 100),
            ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
            BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnYes.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
        btnYes.Click += (_, _) => { ignoreCache = true; cacheForm.DialogResult = System.Windows.Forms.DialogResult.OK; cacheForm.Close(); };
        cacheForm.Controls.Add(btnYes);
        cacheForm.AcceptButton = btnYes;

        var btnNo = new System.Windows.Forms.Button
        {
            Text = "No",
            Size = new System.Drawing.Size(80, 32),
            Location = new System.Drawing.Point(240, 100),
            ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
            BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
        };
        btnNo.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
        btnNo.Click += (_, _) => { ignoreCache = false; cacheForm.DialogResult = System.Windows.Forms.DialogResult.OK; cacheForm.Close(); };
        cacheForm.Controls.Add(btnNo);
        cacheForm.CancelButton = btnNo;

        cacheForm.ShowDialog(parent);
        return ignoreCache;
    }
}
#endif

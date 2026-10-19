using System;
using System.Drawing;
using System.Windows.Forms;

namespace Discphew.UI
{
    /// <summary>Dark replacements for MessageBox.</summary>
    static class Dialogs
    {
        public static void Info(IWin32Window owner, string title, string text) => Show(owner, title, text, "OK", null, false);
        public static void Error(IWin32Window owner, string title, string text) => Show(owner, title, text, "OK", null, true);
        public static bool Confirm(IWin32Window owner, string title, string text, string ok = "Continue", bool danger = false) =>
            Show(owner, title, text, ok, "Cancel", danger);

        static bool Show(IWin32Window owner, string title, string text, string ok, string cancel, bool danger)
        {
            using var f = new Form
            {
                Text = "discphew",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                AutoScaleMode = AutoScaleMode.None,
                KeyPreview = true,
            };
            int w = Theme.S(440), pad = Theme.S(24);
            var heading = new Caption(title, Theme.Heading, Theme.Text) { Bounds = new Rectangle(pad, pad, w - pad * 2, Theme.S(28)) };
            var size = TextRenderer.MeasureText(text, Theme.Body, new Size(w - pad * 2, 0), TextFormatFlags.WordBreak);
            var body = new Label
            {
                Text = text, ForeColor = Theme.Muted, BackColor = Color.Transparent, UseMnemonic = false,
                Bounds = new Rectangle(pad, heading.Bottom + Theme.S(8), w - pad * 2, size.Height + Theme.S(4)),
            };
            var okButton = new FlatButton(ok) { Primary = !danger, Danger = danger };
            okButton.Width = Math.Max(Theme.S(96), okButton.PreferredWidth);
            okButton.Location = new Point(w - pad - okButton.Width, body.Bottom + Theme.S(20));
            okButton.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.AddRange(new Control[] { heading, body, okButton });
            if (cancel != null)
            {
                var cancelButton = new FlatButton(cancel);
                cancelButton.Width = Math.Max(Theme.S(96), cancelButton.PreferredWidth);
                cancelButton.Location = new Point(okButton.Left - Theme.S(8) - cancelButton.Width, okButton.Top);
                cancelButton.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
                f.Controls.Add(cancelButton);
            }
            f.ClientSize = new Size(w, okButton.Bottom + pad);
            f.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); }
                if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; f.Close(); }
            };
            f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
            f.Shown += (s, e) => okButton.Focus();
            return f.ShowDialog(owner) == DialogResult.OK;
        }
    }
}

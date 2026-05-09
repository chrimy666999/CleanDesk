using System.Diagnostics;
using System.Globalization;
using System.Text;
using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public static class ShellContextMenuHost
{
    private const string Command = "--shell-menu";

    public static bool TryRunHelper(string[] args)
    {
        if (args.Length < 4 || !args[0].Equals(Command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var path = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            var x = int.Parse(args[2], CultureInfo.InvariantCulture);
            var y = int.Parse(args[3], CultureInfo.InvariantCulture);

            using var owner = new Forms.Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = Forms.FormBorderStyle.None,
                StartPosition = Forms.FormStartPosition.Manual,
                Size = new System.Drawing.Size(1, 1),
                Location = new System.Drawing.Point(x, y),
                Opacity = 0
            };

            owner.Show();
            Forms.Application.DoEvents();
            ShellContextMenu.Show(path, owner.Handle, x, y);
            owner.Close();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Shell context menu helper failed.");
        }

        return true;
    }

    public static bool ShowOutOfProcess(string path, int x, int y)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Forms.Application.ExecutablePath;
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(Command);
            start.ArgumentList.Add(encoded);
            start.ArgumentList.Add(x.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(y.ToString(CultureInfo.InvariantCulture));
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start shell context menu helper.");
            return false;
        }
    }
}

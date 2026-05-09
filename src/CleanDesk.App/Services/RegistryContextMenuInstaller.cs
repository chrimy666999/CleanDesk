using Microsoft.Win32;

namespace CleanDesk.App.Services;

public static class RegistryContextMenuInstaller
{
    private const string Root = @"Software\Classes\Directory\Background\shell\CleanDesk";

    public static void Install()
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            using var root = Registry.CurrentUser.CreateSubKey(Root);
            root.SetValue("MUIVerb", "CleanDesk");
            root.SetValue("Icon", $"\"{exe}\",0");
            root.SetValue("SubCommands", "");

            AddCommand(root, "CreateBox", "创建盒子", "--create-box");
            AddCommand(root, "CreateMappedBox", "创建映射盒子", "--create-mapped-box");
            AddCommand(root, "Organize", "自动整理桌面", "--organize");
            AddCommand(root, "Restore", "一键恢复桌面", "--restore");
            AddCommand(root, "Settings", "设置中心", "--settings");
            AddCommand(root, "Pause", "暂停整理", "--pause");
            AddCommand(root, "Exit", "退出应用", "--exit");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to install desktop context menu.");
        }
    }

    private static void AddCommand(RegistryKey root, string keyName, string title, string argument)
    {
        var exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        using var item = root.CreateSubKey(@"shell\" + keyName);
        item.SetValue("MUIVerb", title);
        item.SetValue("Icon", $"\"{exe}\",0");
        using var command = item.CreateSubKey("command");
        command.SetValue("", $"\"{exe}\" {argument}");
    }
}

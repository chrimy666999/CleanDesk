using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualBasic.FileIO;
using Forms = System.Windows.Forms;

namespace CleanDesk.App.Services;

public static class ShellOperations
{
    public static void Open(string path)
    {
        if (!Exists(path))
        {
            Forms.MessageBox.Show("文件或文件夹不存在，可能已经被移动或删除。", "CleanDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public static void OpenContainingFolder(string path)
    {
        if (!Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    public static void ShowProperties(string path)
    {
        if (!Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Verb = "properties",
            UseShellExecute = true
        });
    }

    public static void CopyPath(string path)
    {
        Forms.Clipboard.SetText(path);
    }

    public static void CopyFileDrop(string path, bool cut)
    {
        CopyFileDrop([path], cut);
    }

    public static void CopyFileDrop(IEnumerable<string> paths, bool cut)
    {
        var existingPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existingPaths.Length == 0)
        {
            return;
        }

        var files = new StringCollection();
        files.AddRange(existingPaths);
        var data = new Forms.DataObject();
        data.SetFileDropList(files);
        using var stream = new MemoryStream([(byte)(cut ? 2 : 5), 0, 0, 0]);
        data.SetData("Preferred DropEffect", stream);
        Forms.Clipboard.SetDataObject(data, true);
    }

    public static void DeleteToRecycleBin(string path)
    {
        if (!Exists(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    public static bool Rename(string path, string newName)
    {
        if (!Exists(path) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var target = Path.Combine(directory, newName.Trim());
        if (Exists(target))
        {
            Forms.MessageBox.Show("目标名称已经存在。", "CleanDesk", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return false;
        }

        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        return true;
    }

    public static bool Exists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }
}
